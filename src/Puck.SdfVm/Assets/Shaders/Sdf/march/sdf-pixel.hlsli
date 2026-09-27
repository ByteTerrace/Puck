// The pixel a hit stage runs for: the view, the camera ray, the tile's march bounds and instance mask, and the pixel's
// footprint and place in its workgroup, gathered once by the hit passes' entry point for whichever stage it compiles.
#ifndef MARCH_SDF_PIXEL_HLSLI
#define MARCH_SDF_PIXEL_HLSLI
#if defined(SDF_PRIMARY_PASS) || defined(SDF_PRIMARY_READ)

struct SdfPixel {
    ViewportData view;
    float3 rayOrigin;
    float3 rayDirection;
    int viewMode;
    float farDistance;
    // The tile's march start, raised to the view's near plane (worldNearRayDistance), or TileEmpty for a tile the beam
    // proved empty and for a lane past the render extent.
    float marchStart;
    // The four-bound teleport's proven-empty gap for this tile (planes 1/2; sdf-beam wrote them). firstExit = the far
    // distance when no gap was proven, so the teleport is a dead branch.
    float firstExit;
    float secondEntry;
    // The far bound (plane 3; sdf-beam wrote it): the depth past which this tile's cone cannot produce any hit the fine
    // march would accept (proven against the footprint-inflated threshold), so the march exits there. The far distance
    // means no bound was proven, and the far-bound lever pushes it out of reach, so the march runs to the far distance.
    float farBound;
    // Where the tile's instance mask starts, at the host-pushed width the beam prepass wrote it with.
    uint instanceMaskBase;
    // The per-pixel world footprint scale (footprint at distance t = pixelFootprint * t): the viewport's vertical field of
    // view (2 * tan(fov/2)) spread over its pixel height, feeding the march's resolution-independent hit threshold. It is
    // a pixel diameter, twice the pixel radius Keinert's termination test names: half a pixel of conservative silhouette,
    // in the same direction as the Lipschitz clamp's bias.
    float pixelFootprint;
    uint2 pixel;
    uint viewIndex;
    // The lane's index in its 8x8 workgroup (the dispatch box's origin is group-aligned), and whether it owns a rendered
    // pixel. A lane past the render extent still reaches a stage's group barriers (uniform control flow) and stores
    // nothing.
    uint lane;
    bool active;
};

// Gathers the pixel at `pixel` of the view the set renders, whose index is `viewIndex`. Tile reads are clamped onto the
// render extent, so an inactive lane's index never leaves the tile grid.
SdfPixel sdfPixelAt(ViewportData view, uint2 pixel, uint viewIndex) {
    // The render extent: the view's output image's size (worldViewDims, the value the beam and instance-cull tile coverage
    // read too).
    uint2 rectDims = worldViewDims(view);
    SdfPixel p;

    p.view = view;
    p.pixel = pixel;
    p.viewIndex = viewIndex;
    p.active = ((pixel.x < rectDims.x) && (pixel.y < rectDims.y));
    p.lane = (((pixel.y & 7u) * 8u) + (pixel.x & 7u));

    uint2 clampedPixel = min(pixel, (rectDims - uint2(1u, 1u)));
    float2 localUv = ((float2(pixel) + 0.5) / float2(rectDims));
    uint2 tileCoord = (clampedPixel / WorldTileSize);
    uint tileIndex = worldTileIndex(viewIndex, tileCoord, passGroup.tileGrid);
    float marchStart = (p.active ? tiles[worldTileMarchStartIndex(tileIndex)] : TileEmpty);

    p.firstExit = tiles[worldTileFirstExitIndex(tileIndex)];
    p.secondEntry = tiles[worldTileSecondEntryIndex(tileIndex)];
    p.farBound = tiles[worldTileFarBoundIndex(tileIndex)];

    if (worldFarBoundDisabled()) {
        p.farBound = (worldFarDistance(view) + 1.0);
    }

    p.instanceMaskBase = worldInstanceMaskBase(tileIndex);
    p.pixelFootprint = ((2.0 * view.right.w) / max(float(rectDims.y), 1.0));
    p.rayOrigin = view.position.xyz;
    p.rayDirection = cameraRayDirection(view, localUv);
    p.viewMode = (int)round(view.forward.w);
    p.farDistance = worldFarDistance(view);

    // Cone entry is a conservative ray distance; the ray starts on the near plane, where rasterization clips too.
    if (marchStart >= 0.0) {
        marchStart = max(marchStart, worldNearRayDistance(view, p.rayDirection));
    }

    p.marchStart = marchStart;

    return p;
}

#endif
#endif
