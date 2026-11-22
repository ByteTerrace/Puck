// The pixel a pixel pass's stage runs for: the view, the camera ray and the pixel's footprint, gathered once by the
// pixel passes' entry point for whichever stage it compiles.
#ifndef FRAME_SDF_PIXEL_HLSLI
#define FRAME_SDF_PIXEL_HLSLI

struct SdfPixel {
    ViewportData view;
    float3 rayOrigin;
    float3 rayDirection;
    int viewMode;
    float farDistance;
    // The per-pixel world footprint scale (footprint at distance t = pixelFootprint * t): the viewport's vertical field of
    // view (2 * tan(fov/2)) spread over its pixel height.
    float pixelFootprint;
    uint2 pixel;
    uint viewIndex;
    // Whether the lane owns a rendered pixel: the dispatch rounds the render extent up to whole workgroups.
    bool active;
};

// The point a pixel's ray reaches at ray distance `t`, `precise` so every pass that forms a hit's surface point from the
// record gets the same bits: views shades at it, the binned filter averages about it, and the probe lookup gathers at it
// for views to read.
float3 sdfPixelSurfacePoint(SdfPixel p, float t) {
    precise float3 rayTravel = (p.rayDirection * t);
    precise float3 surfacePoint = (p.rayOrigin + rayTravel);

    return surfacePoint;
}
// Gathers the pixel at `pixel` of the view the set renders, whose index is `viewIndex`.
SdfPixel sdfPixelAt(ViewportData view, uint2 pixel, uint viewIndex) {
    uint2 rectDims = worldViewDims(view);
    SdfPixel p;

    p.view = view;
    p.pixel = pixel;
    p.viewIndex = viewIndex;
    p.active = ((pixel.x < rectDims.x) && (pixel.y < rectDims.y));
    p.pixelFootprint = ((2.0 * view.right.w) / max(float(rectDims.y), 1.0));
    p.rayOrigin = view.position.xyz;
    p.rayDirection = cameraRayDirection(view, ((float2(pixel) + 0.5) / float2(rectDims)));
    p.viewMode = (int)round(view.forward.w);
    p.farDistance = worldFarDistance(view);

    return p;
}

#endif
