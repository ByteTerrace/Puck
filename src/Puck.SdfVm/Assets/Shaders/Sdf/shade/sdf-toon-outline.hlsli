// What a cel style reads of a pixel's neighbours in the visibility records: its ink and the normal its band reads. Ink
// belongs to the foreground surface, before temporal reprojection and atmosphere. Never ink a screen's picture or a
// portal image: the view beyond has already shaded its own surfaces and their silhouettes.
//
// Ink marks two edges, each as a measure that grows smoothly with the edge's strength rather than a test, so a jittered
// sample on either side of a threshold moves the ink a little instead of switching it, and the temporal resolve
// converges it to a coverage:
// - a silhouette: a neighbour behind this pixel's tangent plane by a share of the viewing distance
//   (SdfToonSilhouetteNear to SdfToonSilhouetteFar), so a raised plate or a panel's lip, a few millimetres proud of the
//   part it sits on, stays a surface of that part at any distance and only a body standing apart from what lies behind
//   it is outlined; only the near side inks;
// - a crease: the two neighbours on opposite sides of the pixel turned apart by more than the style's crease cosine
//   while both continue this pixel's surface, so a corner between two faces inks and a step between two parallel faces
//   (a plate's edge, a paint band's lip) does not.
// A paint boundary changes neither depth nor normal and draws none.
#ifndef SHADE_SDF_TOON_OUTLINE_HLSLI
#define SHADE_SDF_TOON_OUTLINE_HLSLI

// A silhouette's depth step, as a share of the viewing distance, over which its ink grows from none to full.
static const float SdfToonSilhouetteNear = 0.01;
static const float SdfToonSilhouetteFar = 0.03;
// The width, as a cosine, over which a crease's ink grows about the style's crease cosine.
static const float SdfToonCreaseWidth = 0.1;

struct SdfToonNeighbour {
    bool inside;
    bool sky;
    float t;
    float3 position;
    float3 normal;
};
SdfToonNeighbour sdfToonNeighbour(SdfPixel p, int2 offset) {
    SdfToonNeighbour n = (SdfToonNeighbour)0;
    int2 pixel = int2(p.pixel) + offset;
    int2 dims = int2(worldViewDims(p.view));
    n.inside = (all(pixel >= 0) && all(pixel < dims));
    if (!n.inside) { return n; }
    uint record = worldVisibilityRecord(uint2(pixel), p.viewIndex);
    n.t = asfloat(sdfVisibilityRecordBuffer[record + SdfVisibilityTWord]);
    n.sky = (n.t >= p.farDistance);
    if (n.sky) { return n; }
    n.normal = sdfVisibilityUnpackNormal(sdfVisibilityRecordBuffer[record + SdfVisibilityNormalWord]);
    n.position = p.rayOrigin + cameraRayDirection(p.view, (float2(pixel) + 0.5) / float2(dims)) * n.t;
    return n;
}

// How far a neighbour lies off this pixel's tangent plane, as a share of the viewing distance.
float sdfToonDepthStep(SdfSurfaceSample s, float3 surfacePoint, SdfToonNeighbour n) {
    return (abs(dot(n.position - surfacePoint, s.normal)) / max(s.t, 1.0e-4));
}
// A step smaller than a pixel or two (p.pixelFootprint is a pixel's width per unit of distance) is the raster's.
float sdfToonSilhouette(SdfPixel p, SdfSurfaceSample s, float3 surfacePoint, SdfToonNeighbour n) {
    if (!n.inside) { return 0.0; }
    if (n.sky) { return 1.0; }
    float footprintShare = (1.5 * p.pixelFootprint);
    float stepShare = sdfToonDepthStep(s, surfacePoint, n);
    return ((n.t > s.t) ? (smoothstep(SdfToonSilhouetteNear, SdfToonSilhouetteFar, stepShare) * smoothstep(footprintShare, 2.0 * footprintShare, stepShare)) : 0.0);
}
float sdfToonCrease(SdfToonStyle style, SdfSurfaceSample s, float3 surfacePoint, SdfToonNeighbour a, SdfToonNeighbour b) {
    if (!a.inside || !b.inside || a.sky || b.sky) { return 0.0; }
    float joined = ((1.0 - smoothstep(SdfToonSilhouetteNear, SdfToonSilhouetteFar, sdfToonDepthStep(s, surfacePoint, a)))
        * (1.0 - smoothstep(SdfToonSilhouetteNear, SdfToonSilhouetteFar, sdfToonDepthStep(s, surfacePoint, b))));
    float turn = dot(a.normal, b.normal);
    return (joined * (1.0 - smoothstep(style.outline.z - SdfToonCreaseWidth, style.outline.z + SdfToonCreaseWidth, turn)));
}

// The ink of the ring of eight neighbours `radius` pixels away. With `gather`, the ring also adds its neighbours'
// normals to `normalSum` and their weights to `weightSum`, each weighted by how near it lies to this pixel's tangent
// plane (within two pixel footprints), so the average never crosses a silhouette.
float sdfToonRing(SdfToonStyle style, SdfPixel p, SdfSurfaceSample s, float3 surfacePoint, int radius, bool gather, inout float3 normalSum, inout float weightSum) {
    float footprint = max(p.pixelFootprint * s.t, 1.0e-4);
    float ink = 0.0;

    // Four directions, each with its opposite: right, up-right, up and up-left.
    [unroll]
    for (int d = 0; d < 4; d++) {
        int2 offset = int2(((d == 0) || (d == 1)) ? 1 : ((d == 3) ? -1 : 0), (d == 0) ? 0 : 1) * radius;
        SdfToonNeighbour a = sdfToonNeighbour(p, offset);
        SdfToonNeighbour b = sdfToonNeighbour(p, -offset);
        ink = max(ink, max(max(sdfToonSilhouette(p, s, surfacePoint, a), sdfToonSilhouette(p, s, surfacePoint, b)),
            sdfToonCrease(style, s, surfacePoint, a, b)));
        if (gather) {
            float wa = ((a.inside && !a.sky) ? saturate(1.0 - abs(dot(a.position - surfacePoint, s.normal)) / (2.0 * footprint)) : 0.0);
            float wb = ((b.inside && !b.sky) ? saturate(1.0 - abs(dot(b.position - surfacePoint, s.normal)) / (2.0 * footprint)) : 0.0);
            normalSum += (wa * a.normal) + (wb * b.normal);
            weightSum += (wa + wb);
        }
    }
    return ink;
}

// What a cel style reads around a pixel: the normal its band reads, how far the normals there part (as a cosine change,
// from their mean's shortfall from unit length), and the pixel's ink at the style's width.
struct SdfToonGathered {
    float3 normal;
    float spread;
    float ink;
};
SdfToonGathered sdfToonGather(SdfToonStyle style, SdfPixel p, SdfSurfaceSample s, float3 surfacePoint) {
    SdfToonGathered g;
    float3 normalSum = s.normal;
    float weightSum = 1.0;
    float width = style.outline.x / (1.0 + s.t / max(style.outline.y, 0.1));
    float near = sdfToonRing(style, p, s, surfacePoint, 1, true, normalSum, weightSum);
    float3 mean = normalSum / weightSum;
    float length2 = dot(mean, mean);

    g.normal = ((length2 > 1.0e-8) ? (mean * rsqrt(length2)) : s.normal);
    g.spread = sqrt(2.0 * saturate(1.0 - sqrt(length2)));
    g.ink = 0.0;
    if (width > 0.0) {
        // Two integer radii, interpolated so distance and dynamic resolution never pop.
        int inner = max(1, (int)floor(width));
        float innerInk = ((inner == 1) ? near : sdfToonRing(style, p, s, surfacePoint, inner, false, normalSum, weightSum));
        float outerInk = ((width > 1.0) ? sdfToonRing(style, p, s, surfacePoint, inner + 1, false, normalSum, weightSum) : 0.0);
        g.ink = ((width <= 1.0) ? (innerInk * width) : lerp(innerInk, outerInk, frac(width)));
    }
    return g;
}

#endif
