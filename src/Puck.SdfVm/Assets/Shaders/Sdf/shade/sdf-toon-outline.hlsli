// Ink belongs to the foreground surface, before temporal reprojection and atmosphere. Never ink a screen's picture
// or a portal image: the view beyond has already shaded its own surfaces and their silhouettes.
#ifndef SHADE_SDF_TOON_OUTLINE_HLSLI
#define SHADE_SDF_TOON_OUTLINE_HLSLI

float sdfToonEdge(SdfPixel p, SdfSurfaceSample s, float3 surfacePoint, int2 offset) {
    int2 pixel = int2(p.pixel) + offset;
    int2 dims = int2(worldViewDims(p.view));
    if (any(pixel < 0) || any(pixel >= dims)) { return 0.0; }
    uint record = worldVisibilityRecord(uint2(pixel), p.viewIndex);
    float t = asfloat(sdfVisibilityRecordBuffer[record + SdfVisibilityTWord]);
    if (t >= p.farDistance) { return 1.0; }
    float3 normal = sdfVisibilityUnpackNormal(sdfVisibilityRecordBuffer[record + SdfVisibilityNormalWord]);
    float3 direction = cameraRayDirection(p.view, (float2(pixel) + 0.5) / float2(dims));
    float3 delta = (p.rayOrigin + direction * t) - surfacePoint;
    float footprint = max(p.pixelFootprint * s.t, 0.0001);
    // A tangent-plane comparison avoids drawing a depth edge across a sloping plane. Only the near side inks a jump.
    float depth = smoothstep(1.5, 3.0, abs(dot(delta, s.normal)) / footprint) * step(s.t, t);
    float crease = 1.0 - smoothstep(passGroup.toonOutline.z - 0.08, passGroup.toonOutline.z, dot(s.normal, normal));
    crease *= 1.0 - smoothstep(3.0, 6.0, abs(t - s.t) / footprint);
    return max(depth, crease);
}

float sdfToonOutline(SdfPixel p, SdfSurfaceSample s, float3 surfacePoint) {
    float width = passGroup.toonOutline.x / (1.0 + s.t / max(passGroup.toonOutline.y, 0.1));
    if (width <= 0.0) { return 0.0; }
    int inner = max(1, (int)floor(width));
    int outer = inner + 1;
    float innerEdge = 0.0;
    float outerEdge = 0.0;
    // Constant directions and two integer radii; interpolate radii so distance and dynamic resolution never pop.
    [unroll]
    for (int y = -1; y <= 1; y++) {
        [unroll]
        for (int x = -1; x <= 1; x++) {
            if ((x != 0) || (y != 0)) {
                innerEdge = max(innerEdge, sdfToonEdge(p, s, surfacePoint, int2(x, y) * inner));
                if (width > 1.0) {
                    outerEdge = max(outerEdge, sdfToonEdge(p, s, surfacePoint, int2(x, y) * outer));
                }
            }
        }
    }
    return width <= 1.0 ? innerEdge * width : lerp(innerEdge, outerEdge, frac(width));
}

#endif
