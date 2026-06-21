// Stage 1 of the two-stage SDF world compositor: renders EACH viewport's SDF camera into its own per-view source
// texture (rect-sized, view-local), accelerated by the sdf-beam.comp tile cull. Stage 2 (sdf-world-composite.comp)
// then places each source — an SDF view OR a child node's output, treated uniformly — into its screen region. The
// dispatch is (maxRectWidth, maxRectHeight, viewportCount) over an 8x8 workgroup. KEEP shading IN SYNC with
// sdf-view.frag.hlsl and renderView in sdf-world.hlsli.
// Stage 1 marches map(), so it opts into the per-frame dynamic-entity transform buffer (sdf-vm.hlsli).
#define SDF_DYNAMIC_TRANSFORMS
#include "sdf-world.hlsli"

// The program is at binding 1 (sdf-vm.hlsli, register t0), the viewport table at binding 2 (sdf-world.hlsli,
// register t1), the dynamic-transform buffer at binding 9 (sdf-vm.hlsli, register t2), the read-only cull buffer at
// binding 3 (register u0). The per-view source textures (one per viewport, an array) are LAST at binding 4 so their
// heap slots don't overlap the fixed bindings above on the linear Direct3D 12 descriptor table; Stage 1 writes view N
// into sources[N] at its view-local pixel.
[[vk::binding(3, 0)]] RWStructuredBuffer<float> tiles : register(u0);
[[vk::binding(4, 0)]] [[vk::image_format("rgba8")]] RWTexture2D<float4> sources[4] : register(u1);
// The surviving-tile bbox group origin from the cull-args pass (sdf-cull-args.comp): the dispatch is origin-anchored,
// so this offsets each invocation onto the bbox's pixels. The all-empty margins outside the bbox are never dispatched.
// register(t3): the SRVs are program t0, viewport t1, dynamicTransforms t2, then this.
[[vk::binding(8, 0)]] StructuredBuffer<uint> cullBounds : register(t3);

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    if (id.z >= params.viewportCount) {
        return;
    }

    // A child viewport's source[] slot already holds another node's rendered output (bound by the host); leave it
    // untouched so the SDF render never clobbers the child surface.
    if (isChildViewport(id.z)) {
        return;
    }

    // The indirect dispatch covers the GPU-computed surviving-tile bbox anchored at (0,0); add its group origin (in
    // pixels) so this invocation addresses the bbox's pixel rather than the frame's top-left.
    uint2 pixel = ((uint2(cullBounds[0], cullBounds[1]) * 8u) + id.xy);

    ViewportData view = viewports[id.z];
    uint2 rectDims = (uint2)(view.region.zw * float2(params.imageExtent));

    // Pixels past this viewport's pixel extent fall outside its source texture.
    if ((pixel.x >= rectDims.x) || (pixel.y >= rectDims.y)) {
        return;
    }

    float2 localUv = ((float2(pixel) + 0.5) / float2(rectDims));
    uint2 tileCoord = (pixel / WorldTileSize);
    float marchStart = tiles[worldTileIndex(id.z, tileCoord, params.tileGrid)];
    float3 color = renderView(view, localUv, marchStart);

    sources[id.z][pixel] = float4(color, 1.0);
}
