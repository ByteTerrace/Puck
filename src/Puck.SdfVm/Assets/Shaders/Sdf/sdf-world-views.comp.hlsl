// Stage 1 of the two-stage SDF world compositor: renders EACH viewport's SDF camera into its own per-view source
// texture (rect-sized, view-local), accelerated by the sdf-beam.comp tile cull. Stage 2 (sdf-world-composite.comp)
// then places each source — an SDF view OR a child node's output, treated uniformly — into its screen region. The
// dispatch is (maxRectWidth, maxRectHeight, viewportCount) over an 8x8 workgroup. KEEP shading IN SYNC with
// renderView in sdf-world.hlsli.
// Stage 1 marches map(), so it opts into the per-frame dynamic-entity transform buffer (sdf-vm.hlsli), is the ONLY
// kernel that opts into the screen-source sampling seam (sdf-world.hlsli) — the beam prepass and Stage 2 never bind
// the screen table/sources, so they keep their existing descriptor sets byte-for-byte — and is the ONLY kernel that
// opts into the READ side of the per-tile instance mask (SDF_INSTANCE_MASKS: sdf-vm.hlsli's sdfInstanceMasks at
// binding 7, register t37 — the first SRV slot free of the program/viewport/dynamicTransforms/cullBounds/
// screenSurfaces/screenSources run above (32 screen sources at t5..t36); the beam prepass binds the same buffer as its
// own RW u1 to WRITE it).
#define SDF_DYNAMIC_TRANSFORMS
#define SDF_FRAME_INSTANCE_GRID
#define SDF_FRAME_INSTANCE_GRID_REGISTER t42
#define SDF_INSTANCE_MASKS
#define SDF_SCREEN_SOURCES
// The ONLY kernel that binds the glyph atlas (sdf-vm.hlsli's sdfGlyphAtlas at binding 44 / register t39, sampler s32) —
// so SDF_SHAPE_GLYPH marches the true lettering here while every other kernel sees the conservative cell box.
#define SDF_GLYPH_ATLAS
// The brick pool (sdf-vm.hlsli's sdfBrickPool at binding 46): Stage 1 samples baked SampledRegion carves O(1) so the
// primary/shadow/AO marches stop paying O(carve-count). register(t41): appended LAST in the engine's views binding list
// (after sdfDecalCells t40), and the core-ops variant (sdf-world-views-core.comp) inherits this define through its
// verbatim include, so BOTH Stage 1 variants bind the pool and evaluate bricks.
#define SDF_SAMPLED_REGIONS
#define SDF_BRICK_POOL_REGISTER t41
#include "sdf-world.hlsli"

// The program is at binding 1 (sdf-vm.hlsli, register t0), the viewport table at binding 2 (sdf-world.hlsli,
// register t1), the dynamic-transform buffer at binding 9 (sdf-vm.hlsli, register t2), the read-only cull buffer at
// binding 3 (register u0). The per-view source textures (one per viewport, an array) are LAST at binding 4 so their
// heap slots don't overlap the fixed bindings above on the linear Direct3D 12 descriptor table; Stage 1 writes view N
// into sources[N] at its view-local pixel.
[[vk::binding(3, 0)]] RWStructuredBuffer<float> tiles : register(u0);
[[vk::binding(4, 0)]] [[vk::image_format("rgba8")]] RWTexture2D<float4> sources[5] : register(u1);
// The surviving-tile bbox group origin from the cull-args pass (sdf-cull-args.comp): the dispatch is origin-anchored,
// so this offsets each invocation onto the bbox's pixels. The all-empty margins outside the bbox are never dispatched.
// register(t3): the SRVs are program t0, viewport t1, dynamicTransforms t2, then this. The screen-surface table
// (binding 10, t4) and screen sources (binding 12..43, t5..t36, with the static nearest sampler at s0..s31) are
// declared by sdf-world.hlsli under SDF_SCREEN_SOURCES.
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

    // The symmetry-LOD origin: this viewport's camera (the per-sample wallpaper LOD rule measures from it).
    sdfLodOrigin = view.position.xyz;
    // The per-invocation program-layout cache (sdf-vm.hlsli) — renderView's primary march (<=160 steps), shadow
    // march (<=48 steps), AO taps, and normal dual all call mapMasked/mapGradMasked per step, so the decode must
    // happen exactly once here, before renderView runs.
    sdfProgramLayout = sdfLoadProgramLayout();

    // The RENDER extent: the output rect reduced by the view's render scale (worldRenderDims — the identical integer
    // derivation the beam/instance-cull tile coverage and Stage 2's upsample use). The ray grid spans the SAME frustum
    // over fewer pixels; Stage 2 upsamples the reduced source back into the full region. q = 255 renders native.
    uint2 rectDims = worldRenderDims((uint2)(view.region.zw * float2(params.imageExtent)), view.renderScale.x);

    // Pixels past this viewport's RENDER extent fall outside its rendered source area.
    if ((pixel.x >= rectDims.x) || (pixel.y >= rectDims.y)) {
        return;
    }

    float2 localUv = ((float2(pixel) + 0.5) / float2(rectDims));
    uint2 tileCoord = (pixel / WorldTileSize);
    uint tileIndex = worldTileIndex(id.z, tileCoord, params.tileGrid);
    float marchStart = tiles[worldTileMarchStartIndex(tileIndex)];
    // The four-bound teleport's proven-empty gap for this tile (planes 1/2; sdf-beam wrote them). firstExit =
    // MaxDistance when no gap was proven — the teleport in renderView is then a dead branch.
    float firstExit = tiles[worldTileFirstExitIndex(tileIndex)];
    float secondEntry = tiles[worldTileSecondEntryIndex(tileIndex)];
    // The F1 far bound (plane 3; sdf-beam wrote it): the depth past which this tile's cone cannot produce any hit the
    // fine march would ACCEPT (proven against the footprint-inflated threshold), so renderView exits the march there.
    // MaxDistance = no bound proven (a dead far-exit past the far plane). The A/B lever pushes it out of reach so the
    // "off" side marches exactly as pre-F1.
    float farBound = tiles[worldTileFarBoundIndex(tileIndex)];

    if (worldFarBoundDisabled()) {
        farBound = (MaxDistance + 1.0);
    }
    // The tile's mask BASE (not the words themselves), using the same host-pushed width the beam prepass wrote with.
    uint instanceMaskBase = worldInstanceMaskBase(tileIndex);
    // The per-pixel world footprint scale (footprint at distance t = pixelFootprint * t): the viewport's vertical field
    // of view (2 * tan(fov/2)) spread over its pixel height, feeding renderView's resolution-independent hit threshold.
    // This is a pixel DIAMETER, deliberately 2x the pixel radius Keinert's termination test names — a half-pixel of
    // conservative silhouette, in the same direction as the Lipschitz clamp's bias.
    float pixelFootprint = ((2.0 * view.right.w) / max(float(rectDims.y), 1.0));
    float3 color = renderView(view, localUv, marchStart, firstExit, secondEntry, farBound, instanceMaskBase, pixelFootprint);

    // Dither before the 8-bit store to break gradient banding (sky, distance fog) into blue-ish high-frequency noise:
    // +-0.5 LSB from the integer R2 dither, so BOTH backends add the identical pattern and cross-backend parity holds.
    color += ((sdfR2Dither(pixel) - 0.5) * DitherQuantum);

    sources[id.z][pixel] = float4(color, 1.0);
}
