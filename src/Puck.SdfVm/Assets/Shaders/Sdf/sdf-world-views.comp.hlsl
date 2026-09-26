// Shared dispatch for primary, surface, ambient and views. Each wrapper selects its pass macro; views reads the
// resulting visibility records and shades the set's view into its own output image. SDF_MONOLITHIC_VIEWS retains the
// combined reference walk. The render graph's place pass puts each view's output into its rect. All four hit passes use
// an 8x8 workgroup and identical indirect tile bbox, camera, masks and active-pixel tests.
// The shared layout carries dynamic transforms, screen sources, and the read-only instance mask (binding 7 / t37,
// after screen sources t5..t36). Instance-cull produces that mask; the beam reads it at its own t3 binding.
// Primary, surface and ambient write the visibility records at binding 49 / u1; views reads them at binding 50 / t45
// (sdf-visibility.hlsli). Unused shading resources compile out of primary traversal.
#define SDF_DYNAMIC_TRANSFORMS
#if !defined(SDF_PRIMARY_PASS) && !defined(SDF_MONOLITHIC_VIEWS)
#define SDF_PRIMARY_READ
#endif
#define SDF_FRAME_INSTANCE_GRID
#define SDF_FRAME_INSTANCE_GRID_REGISTER t42
#define SDF_INSTANCE_MASKS
#define SDF_SCREEN_SOURCES
// Primary and views bind the glyph atlas (binding 44 / t39, sampler s32), so primary marches true lettering.
// The beam and other non-atlas kernels retain the conservative cell box.
#define SDF_GLYPH_ATLAS
// The brick pool (binding 46 / t41, after sdfDecalCells t40): primary and shading sample baked SampledRegion carves
// O(1), so primary/shadow/AO marches stop paying O(carve-count). All views variants inherit this binding.
#define SDF_SAMPLED_REGIONS
#define SDF_BRICK_POOL_REGISTER t41
// The bounded-volume buffer binds at 48 / t43 after the frame instance grid t42. It is in the shared descriptor
// layout, but only shading uses shade-volumes.hlsli's call at the end of renderView; primary compiles it out.
// The per-tile shadow gather (sdf-world.hlsli's sdfShadowGatherGroup): one groupshared shadow candidate mask per 8x8
// workgroup, built cooperatively at the uniform seam inside renderView. Every lane — rendered pixel or not — must
// reach renderView, so CSMain below turns its per-pixel extent test into an `active` flag instead of a return.
// Primary and surface exit before group gathers; ambient and views each execute their own uniform gather.
#define SDF_GROUP_SHADOW_GATHER
#define SDF_PART_RAY_BOUNDS
// The beam's tile planes and part bounds, read-only in every hit pass. Declared before the shared world include so
// primary traversal can read the appended part bounds. register(t44): the engine appends it after the bounded
// volumes (t43) in the views binding order.
[[vk::binding(3, 0)]] StructuredBuffer<float> tiles : register(t44);
// The surviving-tile bbox from the cull-args pass (sdf-cull-args.comp): its group origin, then its exclusive group end.
// The dispatch is origin-anchored, so the origin offsets each invocation onto the bbox's pixels, and the all-empty
// margins outside the bbox are never dispatched; the whole box is where this frame wrote visibility records, which
// worldVisibilityCurrent reads. Declared before the shared world include for that reader. register(t3): the SRVs are
// program t0, viewport t1, dynamicTransforms t2, then this. The screen-surface table (binding 10, t4) and screen
// sources (binding 12..43, t5..t36, with the static nearest sampler at s0..s31) are declared by sdf-world.hlsli under
// SDF_SCREEN_SOURCES.
[[vk::binding(8, 0)]] StructuredBuffer<uint> cullBounds : register(t3);
#include "sdf-world.hlsli"

// The program is at binding 1 (sdf-vm.hlsli, register t0), the viewport table at binding 2 (sdf-world.hlsli,
// register t1), the dynamic-transform buffer at binding 9 (sdf-vm.hlsli, register t2). The set's view's output image at
// binding 4 is the layout's first UAV (u0); Stage 1 writes the view's pixels into it at their view-local coordinates.
[[vk::binding(4, 0)]] [[vk::image_format("rgba8")]] RWTexture2D<float4> output : register(u0);

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    if (params.sampleIndex == SDF_ISA_REPORT_REQUEST) {
        if (all(id == uint3(0, 0, 0))) {
            output[uint2(0, 0)] = (float4(0x53u, 0x44u, asuint(tiles[0]), SDF_ISA_VERSION) / 255.0);
        }
        return;
    }

    uint viewIndex = worldViewOf(id.z);

    if (viewIndex >= params.viewportCount) {
        return;
    }

    // The indirect dispatch covers the GPU-computed surviving-tile bbox anchored at (0,0); add its group origin (in
    // pixels) so this invocation addresses the bbox's pixel rather than the frame's top-left.
    uint2 pixel = ((uint2(cullBounds[0], cullBounds[1]) * 8u) + id.xy);

    ViewportData view = viewports[viewIndex];

    // The symmetry-LOD origin: this viewport's camera (the per-sample wallpaper LOD rule measures from it).
    sdfLodOrigin = view.position.xyz;
    // The per-invocation program-layout cache (sdf-vm.hlsli): primary/shadow marches, AO taps and normal queries
    // repeatedly call the field evaluators. Decode once for this invocation before renderView runs.
    sdfProgramLayout = sdfLoadProgramLayout();
    sdfPartBoundsViewport = viewIndex;

    // The RENDER extent: the view's output image's size (worldViewDims, the value the beam and instance-cull tile
    // coverage read too).
    uint2 rectDims = worldViewDims(view);

    // Pixels past this viewport's RENDER extent fall outside its rendered source area. NOT a return: the lane still
    // has to reach the group shadow gather's barriers inside renderView (uniform control flow), so it runs the
    // march-free prologue as an inactive lane and stores nothing. Its tile reads are clamped onto the extent so no
    // index leaves the tile grid.
    bool active = ((pixel.x < rectDims.x) && (pixel.y < rectDims.y));
    uint lane = (((pixel.y & 7u) * 8u) + (pixel.x & 7u)); // == SV_GroupThreadID: the bbox origin is group-aligned
    uint2 clampedPixel = min(pixel, (rectDims - uint2(1u, 1u)));

    float2 localUv = ((float2(pixel) + 0.5) / float2(rectDims));
    uint2 tileCoord = (clampedPixel / WorldTileSize);
    uint tileIndex = worldTileIndex(viewIndex, tileCoord, params.tileGrid);
    float marchStart = (active ? tiles[worldTileMarchStartIndex(tileIndex)] : TileEmpty);
    // The four-bound teleport's proven-empty gap for this tile (planes 1/2; sdf-beam wrote them). firstExit = the
    // far distance when no gap was proven — the teleport in renderView is then a dead branch.
    float firstExit = tiles[worldTileFirstExitIndex(tileIndex)];
    float secondEntry = tiles[worldTileSecondEntryIndex(tileIndex)];
    // The F1 far bound (plane 3; sdf-beam wrote it): the depth past which this tile's cone cannot produce any hit the
    // fine march would ACCEPT (proven against the footprint-inflated threshold), so renderView exits the march there.
    // The far distance (the view's authored far plane, worldFarDistance) = no bound proven (a dead far-exit past the
    // far plane). The A/B lever pushes it out of reach so the "off" side marches exactly as pre-F1.
    float farBound = tiles[worldTileFarBoundIndex(tileIndex)];

    if (worldFarBoundDisabled()) {
        farBound = (worldFarDistance(view) + 1.0);
    }
    // The tile's mask BASE (not the words themselves), using the same host-pushed width the beam prepass wrote with.
    uint instanceMaskBase = worldInstanceMaskBase(tileIndex);
    // The per-pixel world footprint scale (footprint at distance t = pixelFootprint * t): the viewport's vertical field
    // of view (2 * tan(fov/2)) spread over its pixel height, feeding renderView's resolution-independent hit threshold.
    // This is a pixel DIAMETER, deliberately 2x the pixel radius Keinert's termination test names — a half-pixel of
    // conservative silhouette, in the same direction as the Lipschitz clamp's bias.
    float pixelFootprint = ((2.0 * view.right.w) / max(float(rectDims.y), 1.0));

    float3 color = renderView(view, localUv, marchStart, firstExit, secondEntry, farBound, instanceMaskBase, pixelFootprint, pixel, viewIndex, lane, active);

    if (!active) {
        return;
    }

#if !defined(SDF_PRIMARY_PASS) && !defined(SDF_SURFACE_PASS) && !defined(SDF_AMBIENT_PASS)
    // Dither before the 8-bit store to break gradient banding (sky, distance fog) into blue-ish high-frequency noise:
    // +-0.5 LSB from the integer R2 dither, so BOTH backends add the identical pattern and cross-backend parity holds.
    color += ((sdfR2Dither(pixel) - 0.5) * DitherQuantum);

    output[pixel] = float4(color, 1.0);
#endif
}
