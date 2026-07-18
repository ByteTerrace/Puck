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
    // Per-view render-scale numerators q (1..255; 255 = native), 8 bits each: view v's q = (scaleQPacked[v / 4] >>
    // ((v % 4) * 8)) & 0xFF. Mirrors ViewportData.renderScale.x (Stage 1 renders at worldRenderDims(rectDims, q));
    // this kernel upsamples the reduced source back into the full region. KEEP IN SYNC with BuildCompositePush.
    uint2 scaleQPacked;
    // Per-view reconstruction blend, packed exactly like scaleQPacked: 0 = the existing four-tap bilinear fast path,
    // 255 = clamped Catmull-Rom, intermediate bytes blend continuously. KEEP IN SYNC with BuildCompositePush.
    uint2 sharpnessQPacked;
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

float4 catmullRomWeights(float t) {
    float t2 = (t * t);
    float t3 = (t2 * t);

    return float4(
        ((-0.5 * t) + t2 - (0.5 * t3)),
        (1.0 - (2.5 * t2) + (1.5 * t3)),
        ((0.5 * t) + (2.0 * t2) - (1.5 * t3)),
        ((-0.5 * t2) + (0.5 * t3))
    );
}

// A quality-path reconstruction tap. Unlike the historical bilinear path, a wide cubic footprint can cross into a
// beam-culled tile whose source pixels were deliberately not dispatched; substitute the same flat empty color instead
// of sampling stale device memory. Child slots have non-empty tile markers and pass through normally.
float3 reconstructionTap(uint viewIndex, int2 pixel, uint2 renderDims, uint2 tileGrid) {
    uint2 p = (uint2)clamp(pixel, int2(0, 0), (int2(renderDims) - 1));
    uint2 tileCoord = (p / WorldTileSize);

    return (tiles[worldTileIndex(viewIndex, tileCoord, tileGrid)] == TileEmpty)
        ? EmptyTileColor
        : sources[viewIndex][p].rgb;
}

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
            uint q = ((params.scaleQPacked[v / 4u] >> ((v % 4u) * 8u)) & 0xFFu);
            uint sharpnessQ = ((params.sharpnessQPacked[v / 4u] >> ((v % 4u) * 8u)) & 0xFFu);

            if (q >= 255u) {
                // NATIVE (q = 255): the exact-copy path, byte-identical to the pre-render-scale kernel.
                uint2 tileCoord = (localPixel / WorldTileSize);

                // EXACTLY TileEmpty => this SDF tile was culled (its source is stale/undispatched) => flat constant. A
                // surviving SDF tile (>= 0) OR a child viewport's slot (which sdf-beam.comp writes 0.0 into, precisely so
                // this test never reads undefined device memory) copies the source — so child surfaces pass through
                // untouched without needing the child mask here.
                color = (tiles[worldTileIndex(v, tileCoord, tileGrid)] == TileEmpty)
                    ? EmptyTileColor
                    : sources[v][localPixel].rgb;
            } else {
                // REDUCED render (q < 255): Stage 1 rendered this view at worldRenderDims (the same integer derivation
                // — KEEP IN SYNC with sdf-world.hlsli's helper); upsample the reduced source over the full region with
                // an explicit reconstruction filter (formatted loads carry no sampler). The tile test keys on the
                // NEAREST source pixel — the beam's tile coverage lives in render space.
                uint2 rectDims = max((uint2)(r.zw * float2(params.imageExtent)), uint2(1u, 1u));
                uint2 renderDims = max((((rectDims * q) + 127u) / 255u), uint2(1u, 1u));
                float2 sourcePos = ((((float2(localPixel) + 0.5) * float2(renderDims)) / float2(rectDims)) - 0.5);
                float2 clamped = clamp(sourcePos, float2(0.0, 0.0), (float2(renderDims) - 1.0));
                uint2 p0 = (uint2)clamped;
                uint2 p1 = min((p0 + 1u), (renderDims - 1u));
                float2 f = (clamped - float2(p0));
                uint2 nearestPx = (uint2)round(clamped);
                uint2 tileCoord = (nearestPx / WorldTileSize);

                if (tiles[worldTileIndex(v, tileCoord, tileGrid)] == TileEmpty) {
                    color = EmptyTileColor;
                } else {
                    if (sharpnessQ == 0u) {
                        // Preserve the existing four-load path exactly when reconstruction quality is off.
                        float3 c00 = sources[v][p0].rgb;
                        float3 c10 = sources[v][uint2(p1.x, p0.y)].rgb;
                        float3 c01 = sources[v][uint2(p0.x, p1.y)].rgb;
                        float3 c11 = sources[v][p1].rgb;

                        color = lerp(lerp(c00, c10, f.x), lerp(c01, c11, f.x), f.y);
                    } else {
                        float4 wx = catmullRomWeights(f.x);
                        float4 wy = catmullRomWeights(f.y);
                        int2 base = int2(p0);
                        // Reuse the four central safe taps in both the bilinear baseline and cubic rows. The quality
                        // path therefore performs exactly 16 source loads, not 16 plus a duplicate central quad.
                        float3 c00 = reconstructionTap(v, (base + int2(0, 0)), renderDims, tileGrid);
                        float3 c10 = reconstructionTap(v, (base + int2(1, 0)), renderDims, tileGrid);
                        float3 c01 = reconstructionTap(v, (base + int2(0, 1)), renderDims, tileGrid);
                        float3 c11 = reconstructionTap(v, (base + int2(1, 1)), renderDims, tileGrid);
                        float3 bilinear = lerp(lerp(c00, c10, f.x), lerp(c01, c11, f.x), f.y);
                        float3 row0 =
                            (wx.x * reconstructionTap(v, (base + int2(-1, -1)), renderDims, tileGrid)) +
                            (wx.y * reconstructionTap(v, (base + int2( 0, -1)), renderDims, tileGrid)) +
                            (wx.z * reconstructionTap(v, (base + int2( 1, -1)), renderDims, tileGrid)) +
                            (wx.w * reconstructionTap(v, (base + int2( 2, -1)), renderDims, tileGrid));
                        float3 row1 =
                            (wx.x * reconstructionTap(v, (base + int2(-1,  0)), renderDims, tileGrid)) +
                            (wx.y * c00) +
                            (wx.z * c10) +
                            (wx.w * reconstructionTap(v, (base + int2( 2,  0)), renderDims, tileGrid));
                        float3 row2 =
                            (wx.x * reconstructionTap(v, (base + int2(-1,  1)), renderDims, tileGrid)) +
                            (wx.y * c01) +
                            (wx.z * c11) +
                            (wx.w * reconstructionTap(v, (base + int2( 2,  1)), renderDims, tileGrid));
                        float3 row3 =
                            (wx.x * reconstructionTap(v, (base + int2(-1,  2)), renderDims, tileGrid)) +
                            (wx.y * reconstructionTap(v, (base + int2( 0,  2)), renderDims, tileGrid)) +
                            (wx.z * reconstructionTap(v, (base + int2( 1,  2)), renderDims, tileGrid)) +
                            (wx.w * reconstructionTap(v, (base + int2( 2,  2)), renderDims, tileGrid));
                        float3 cubic = ((((wy.x * row0) + (wy.y * row1)) + (wy.z * row2)) + (wy.w * row3));
                        float3 neighborhoodMin = min(min(c00, c10), min(c01, c11));
                        float3 neighborhoodMax = max(max(c00, c10), max(c01, c11));

                        // Catmull-Rom's negative lobes recover edge contrast but can ring. Clamp to the central bilinear
                        // neighborhood, then expose a continuous blend so content can choose its preferred crispness.
                        cubic = clamp(cubic, neighborhoodMin, neighborhoodMax);
                        color = lerp(bilinear, cubic, ((float)sharpnessQ / 255.0));
                    }
                }
            }
            break;
        }
    }

    Output[id.xy] = float4(color, 1.0);
}
