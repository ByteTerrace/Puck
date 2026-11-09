// What a cel style reads of a pixel's neighbours in the visibility records: its ink and the form its band reads (the
// surface's macro normal and its shadow visibility filtered over that form, sdfToonForm). Ink belongs to the foreground
// surface, before temporal reprojection and atmosphere. Never ink a screen's picture or a portal image: the view beyond
// has already shaded its own surfaces and their silhouettes.
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
    uint record;
};
SdfToonNeighbour sdfToonNeighbour(SdfPixel p, int2 offset) {
    SdfToonNeighbour n = (SdfToonNeighbour)0;
    int2 pixel = int2(p.pixel) + offset;
    int2 dims = int2(worldViewDims(p.view));
    n.inside = (all(pixel >= 0) && all(pixel < dims));
    if (!n.inside) { return n; }
    uint record = worldVisibilityRecord(uint2(pixel), p.viewIndex);
    n.record = record;
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

// The form a cel band reads: the surface's shape at the style's form radius (bands.w, metres), so relief smaller than
// that radius (a rock's lumps, a baked surface's detail) neither speckles the band nor rags its terminator. The normal is
// derived from positions, not averaged from normals: the chords from the pixel to the surface points about one form
// radius away along each screen axis, whose cross product turns with the surface's large shape and only by a relief's
// height over the radius (a normal average turns with every bump's slope). Each side takes the farthest of three taps
// (the radius, its half and its quarter) that continues the pixel's surface: within SdfToonFormPlane of its plane, as a
// slope over the chord, and within SdfToonFormReach radii, so a silhouette or a body standing in front is never read, and
// a part smaller than the radius reads over the radius it has. Where the two sides' chords turn apart (a corner between
// two faces) the side nearer the pixel's plane stands alone, so a box's face keeps its own normal to its edge; where they
// agree, the chord across both reads. A pixel nearer than two pixels to its form's scale reads the neighbourhood normal.
// The shadow visibilities average the pixel's filtered ones with each tap's own past the views filter's window, so the
// band steps the shadow a form casts rather than the one each bump casts.
static const float SdfToonFormMaxPixels = 48.0;
static const float SdfToonFormPlane = 0.7;
static const float SdfToonFormReach = 4.0;
static const float SdfToonFormAgree = 0.9;
static const int SdfToonFormFilterPixels = 3;

struct SdfToonForm {
    float3 normal;
    float4 shadows;
    float pixels;
};

// One side's chord along a screen direction: the farthest tap of the ladder on the pixel's surface, false when none is.
// A tap past the views filter's window adds its own shadow visibilities to the sums.
bool sdfToonFormSide(SdfPixel p, float3 surfacePoint, float3 reference, int2 direction, float pixels, float worldRadius,
    out float3 chord, inout float4 shadowSum, inout float4 shadowWeight) {
    chord = float3(0.0, 0.0, 0.0);
    [unroll]
    for (int rung = 0; rung < 3; rung++) {
        int offset = max(1, (int)round(pixels / float(1 << rung)));
        SdfToonNeighbour n = sdfToonNeighbour(p, direction * offset);
        if (n.inside && !n.sky) {
            float3 delta = (n.position - surfacePoint);
            float distance = length(delta);
            if ((distance > 1.0e-5) && (abs(dot(delta, reference)) <= (SdfToonFormPlane * distance))
                && (distance <= (SdfToonFormReach * worldRadius))) {
                chord = delta;
                if (offset >= SdfToonFormFilterPixels) {
                    uint word = sdfVisibilityRecordBuffer[n.record + SdfVisibilityShadowWord];
                    float4 queried = sdfShadowQueried(word);
                    shadowSum += (queried * sdfUnpackShadowVisibility(word));
                    shadowWeight += queried;
                }
                return true;
            }
        }
    }
    return false;
}
// The surface's tangent along a screen axis from its two sides' chords, false when neither side continues the surface.
bool sdfToonFormTangent(SdfPixel p, float3 surfacePoint, float3 reference, int2 axis, float pixels, float worldRadius,
    out float3 tangent, inout float4 shadowSum, inout float4 shadowWeight) {
    float3 ahead;
    float3 behind;
    bool hasAhead = sdfToonFormSide(p, surfacePoint, reference, axis, pixels, worldRadius, ahead, shadowSum, shadowWeight);
    bool hasBehind = sdfToonFormSide(p, surfacePoint, reference, -axis, pixels, worldRadius, behind, shadowSum, shadowWeight);
    behind = -behind;
    tangent = (hasAhead ? ahead : behind);
    if (hasAhead && hasBehind) {
        float3 a = normalize(ahead);
        float3 b = normalize(behind);
        tangent = ((dot(a, b) > SdfToonFormAgree) ? (ahead + behind)
            : ((abs(dot(a, reference)) <= abs(dot(b, reference))) ? ahead : behind));
    }
    return (hasAhead || hasBehind);
}
// The pixel's form, given its neighbourhood normal `reference` (sdfToonGather) and its filtered shadow visibilities.
SdfToonForm sdfToonForm(SdfToonStyle style, SdfPixel p, SdfSurfaceSample s, float3 surfacePoint, float3 reference, float4 shadows) {
    SdfToonForm form;
    form.normal = reference;
    form.shadows = shadows;
    form.pixels = 1.0;
    float footprint = max(p.pixelFootprint * s.t, 1.0e-6);
    float pixels = min(style.bands.w / footprint, SdfToonFormMaxPixels);
    if (pixels < 2.0) {
        return form;
    }
    float worldRadius = (pixels * footprint);
    float4 shadowSum = (2.0 * shadows);
    float4 shadowWeight = float4(2.0, 2.0, 2.0, 2.0);
    float3 across;
    float3 down;
    bool hasAcross = sdfToonFormTangent(p, surfacePoint, reference, int2(1, 0), pixels, worldRadius, across, shadowSum, shadowWeight);
    bool hasDown = sdfToonFormTangent(p, surfacePoint, reference, int2(0, 1), pixels, worldRadius, down, shadowSum, shadowWeight);
    bool measured = ((passGroup.shadowSlotCount > 0u) && !worldSoftShadowsDisabled());
    form.shadows = (measured ? (shadowSum / shadowWeight) : shadows);
    if (hasAcross && hasDown) {
        float3 normal = cross(across, down);
        float length2 = dot(normal, normal);
        if (length2 > 1.0e-12) {
            normal *= rsqrt(length2);
            form.normal = ((dot(normal, reference) < 0.0) ? -normal : normal);
            form.pixels = pixels;
        }
    }
    return form;
}

#endif
