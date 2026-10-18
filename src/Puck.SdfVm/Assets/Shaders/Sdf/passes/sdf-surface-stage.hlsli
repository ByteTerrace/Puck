// The surface pass's stage (SDF_SURFACE_PASS), which reads no field. From the mesh target the mesh pass rasterized it
// writes every pixel's whole visibility record (frame/sdf-visibility.hlsli), misses included: the mesh hit, its draw's
// frame slot and its normal, the screen-space ambient occlusion, and the key light's and the second shadow slot's
// visibility from the shadow cascades into K's first two slots. Views then shades the record.
#ifndef PASSES_SDF_SURFACE_STAGE_HLSLI
#define PASSES_SDF_SURFACE_STAGE_HLSLI
#ifdef SDF_SURFACE_PASS

// The screen-space occlusion's world radius, taps and strength. A tap occludes only past the cosine bias and the lift (a
// height above the tangent plane in pixel footprints), which keep a tessellated curve's facets and a baked surface's
// sub-pixel relief from occluding the interpolated normal beside them. The taps stay within the screen radius, which
// bounds how far a near surface's taps scatter through the cache.
static const float SdfMeshAoRadius = 0.75;
static const uint SdfMeshAoSamples = 8u;
static const float SdfMeshAoStrength = 2.5;
static const float SdfMeshAoBias = 0.1;
static const float SdfMeshAoLift = 2.0;
static const float SdfMeshAoMaxPixels = 128.0;
// The shadow filter's taps: a blocker search and the percentage-closer filter, each over a Vogel disk the pixel's
// pattern turns and spaces.
static const uint SdfMeshShadowSearchTaps = 12u;
static const uint SdfMeshShadowFilterTaps = 16u;
static const float SdfMeshShadowMaxTexels = 14.0;
// The filter's least clearance per texel a caster lies beside the receiver, and the lateral distance, in texels, past
// which the light's cone governs instead (sdfMeshShadowGap).
static const float SdfMeshShadowSlopeBias = 2.5;
static const float SdfMeshShadowNearTexels = 1.5;
static const float SdfGoldenAngle = 2.39996323;

// The pixel pattern the occlusion's and the shadows' taps follow: a rotation and a radial offset, each in [0, 1). Each
// pixel takes its cell of a 4x4 ordered-dither tile, so any 4x4 window of pixels holds each of the tile's sixteen
// patterns exactly once, and views averages such a window (sdfViewsFilter in sdf-views-stage.hlsli, KEEP IN SYNC with
// its 4x4 window): on any surface the per-pixel noise cancels to the same sixteen-pattern estimate, leaving no
// screen-locked structure. A temporal view also steps the pattern within its sixteenth by the sample index of its
// eight-sample period, so a converged history averages 128 patterns.
float2 sdfMeshPattern(uint2 pixel) {
    uint x = (pixel.x & 3u);
    uint y = (pixel.y & 3u);
    uint a = (x ^ y);
    // The Bayer cell: the bits of x ^ y and y interleaved, then reversed.
    uint cell = ((((a & 1u) << 3u) | ((y & 1u) << 2u)) | ((a & 2u) | ((y & 2u) >> 1u)));
    uint step = (passGroup.historyFrames & 7u);

    return float2(
        ((float(cell) + (float(step) / 8.0)) / 16.0),
        ((float(((cell * 5u) + 3u) & 15u) + (float((step * 5u) & 7u) / 8.0)) / 16.0)
    );
}
// Screen-space obscurance from the surface points the mesh target holds: taps on a golden-angle spiral over the disk
// the world radius projects to, turned and spaced by the pixel's pattern. Each tap rebuilds the point its pixel's own ray
// reached, so a tap on the pixel's plane lies on it and occludes nothing however the disk falls on the pixel grid; a tap
// above the tangent plane occludes by the cosine of its elevation past the bias, fading to nothing at the radius, so a
// surface farther than the radius in front casts no halo. Unfiltered: views averages it (sdfViewsFilter).
float sdfMeshAmbient(SdfPixel p, float3 surfacePoint, float3 normal, float t) {
    float forward = (t * dot(p.rayDirection, p.view.forward.xyz));
    float footprint = (p.pixelFootprint * forward);
    float pixels = min((min(SdfMeshAoRadius, (0.25 * t)) / footprint), SdfMeshAoMaxPixels);

    if (pixels < 1.0) {
        return 1.0;
    }

    float radius = (pixels * footprint);
    float inverseRadius2 = (1.0 / (radius * radius));
    float2 pattern = sdfMeshPattern(p.pixel);
    float rotation = (pattern.x * 6.2831853);
    float2 dims = float2(worldViewDims(p.view));
    float occlusion = 0.0;

    [loop]
    for (uint index = 0u; (index < SdfMeshAoSamples); index++) {
        float angle = ((float(index) * SdfGoldenAngle) + rotation);
        float reach = (((float(index) + pattern.y) / float(SdfMeshAoSamples)) * pixels);
        float2 tap = floor((float2(p.pixel) + 0.5) + (float2(cos(angle), sin(angle)) * reach));

        if (any(tap < 0.0) || any(tap >= dims)) {
            continue;
        }

        float4 texel = meshVisibility.Load(int3(int2(tap), 0));

        if (texel.y < 1.0) {
            continue;
        }

        float3 offset = ((p.rayOrigin + (cameraRayDirection(p.view, ((tap + 0.5) / dims)) * texel.x)) - surfacePoint);
        float distance2 = dot(offset, offset);

        if (distance2 > 0.0) {
            float elevation = ((dot(offset, normal) - (SdfMeshAoLift * footprint)) * rsqrt(distance2));
            occlusion += (saturate(1.0 - (distance2 * inverseRadius2)) * saturate((elevation - SdfMeshAoBias) / (1.0 - SdfMeshAoBias)));
        }
    }

    return saturate(1.0 - ((SdfMeshAoStrength * occlusion) / float(SdfMeshAoSamples)));
}
// One cascade's light camera, from the pass block's rows.
struct SdfMeshCascade {
    float3 position;
    float halfHeight;
    float3 right;
    float aspect;
    float3 up;
    float near;
    float3 forward;
    float far;
};
SdfMeshCascade sdfMeshCascadeAt(uint cascade) {
    uint row = (cascade * 4u);
    float4 r0 = passGroup.meshShadowCascades[row];
    float4 r1 = passGroup.meshShadowCascades[row + 1u];
    float4 r2 = passGroup.meshShadowCascades[row + 2u];
    float4 r3 = passGroup.meshShadowCascades[row + 3u];
    SdfMeshCascade c;
    c.position = r0.xyz; c.halfHeight = r0.w;
    c.right = r1.xyz; c.aspect = r1.w;
    c.up = r2.xyz; c.near = r2.w;
    c.forward = r3.xyz; c.far = r3.w;
    return c;
}
// The light-space depth of the nearest caster at a cascade texel. The cascade holds the shadow pipeline's reversed-Z
// orthographic depth, linear from one at its near plane to zero at its far plane (sdf-mesh.vert.hlsl), so a texel the
// cascade drew nothing into, left at its clear of zero, reads as the far plane.
float sdfMeshShadowDepth(uint cascade, SdfMeshCascade c, int2 texel) {
    int last = (int(passGroup.meshShadowTexels) - 1);
    int2 clamped = clamp(texel, int2(0, 0), int2(last, last));
    float depth = ((cascade == 0u) ? meshShadowNear.Load(int3(clamped, 0)).x
        : ((cascade == 1u) ? meshShadowFar.Load(int3(clamped, 0)).x : meshShadowSecond.Load(int3(clamped, 0)).x));
    return (c.far - (depth * (c.far - c.near)));
}
// How far a cascade's caster at the texel `offset` texels from the receiver's lies in front of the receiver, less the
// clearance it needs: positive for an occluder. The receiver is extended over its plane to that texel's centre
// (`gradient`, its depth change per texel). The clearance grows with the caster's lateral distance: by `near` texels
// per texel, and past SdfMeshShadowNearTexels by `cone`, the inverse penumbra slope, so a caster outside the light's
// cone over the receiver, which cannot shadow it, does not count. A surface beside the receiver that rises toward the
// light less steeply than `near` (a wall's foot seen from the floor before it) is the texel grid's, not an occluder.
// The blocker search passes zero for both, so it finds every caster the filter might count.
float sdfMeshShadowGap(uint cascade, SdfMeshCascade c, float2 texel, float2 offset, float receiver, float2 gradient, float texelWorld, float near, float cone) {
    float2 at = floor(texel + offset);
    float2 delta = ((at + 0.5) - texel);
    float lateral = length(delta);
    float clearance = (texelWorld * max((near * lateral), ((lateral - SdfMeshShadowNearTexels) * cone)));

    return (((receiver + dot(gradient, delta)) - clearance) - sdfMeshShadowDepth(cascade, c, int2(at)));
}
// A shadowed light's visibility at a surface point from the first of its cascades [first, end) that holds it: a blocker
// search sizes the penumbra from the light's penumbra slope and the gap to the average blocker, and a percentage-closer
// filter of that width answers. Both disks follow the pixel's pattern (sdfMeshPattern), and views averages the answer
// over the pattern's window (sdfViewsFilter). `face` is the hit triangle's own normal: the cascade holds the triangle,
// not the shading normal's smoothed surface, which tilts away from it across a crease the bake rounded.
float sdfMeshShadow(SdfPixel p, float3 surfacePoint, float3 face, uint first, uint end, float slope) {
    float2 pattern = sdfMeshPattern(p.pixel);
    float rotation = (pattern.x * 6.2831853);
    float4 parameters = passGroup.meshShadowParameters;
    float cone = (1.0 / slope);
    float texels = float(passGroup.meshShadowTexels);

    [loop]
    for (uint cascade = first; (cascade < end); cascade++) {
        SdfMeshCascade c = sdfMeshCascadeAt(cascade);
        float texelWorld = ((2.0 * c.halfHeight) / texels);
        float3 biased = (surfacePoint + (face * (parameters.z * texelWorld)));
        float3 relative = (biased - c.position);
        float2 plane = float2((dot(relative, c.right) / (c.aspect * c.halfHeight)), (dot(relative, c.up) / c.halfHeight));

        // A cascade holds the casters between its near and far planes only; a receiver past its far plane (the floor far
        // below a high camera's near slice) would read every empty texel's far depth as a blocker in front of it.
        float depth = dot(relative, c.forward);

        if (any(abs(plane) > 0.97) || (depth <= c.near) || (depth >= c.far)) {
            continue;
        }

        float2 texel = (float2(((plane.x * 0.5) + 0.5), (0.5 - (plane.y * 0.5))) * texels);
        float receiver = (depth - parameters.y);
        // The receiver's plane in the cascade: its depth change per texel along the target's x and y, from the normal in
        // light space (x along right, y down the target's rows), so each tap compares against the receiver's own surface
        // at that tap's texel centre rather than the depth at the pixel. A receiver near edge-on to the light keeps its
        // slope at that of a normal a fifth of the way toward the light.
        float3 lightNormal = float3(dot(face, c.right), dot(face, c.up), min(dot(face, c.forward), -0.2));
        float2 gradient = (float2((-lightNormal.x * c.aspect), lightNormal.y) * (texelWorld / lightNormal.z));
        float searchTexels = SdfMeshShadowMaxTexels;
        float blockers = 0.0;
        float blockerGap = 0.0;

        [loop]
        for (uint tap = 0u; (tap < SdfMeshShadowSearchTaps); tap++) {
            float r = sqrt((float(tap) + pattern.y) / float(SdfMeshShadowSearchTaps));
            float angle = ((float(tap) * SdfGoldenAngle) + rotation);
            float gap = sdfMeshShadowGap(cascade, c, texel, (float2(cos(angle), sin(angle)) * (r * searchTexels)), receiver, gradient, texelWorld, 0.0, 0.0);
            if (gap > 0.0) {
                blockers += 1.0;
                blockerGap += gap;
            }
        }

        if (blockers <= 0.0) {
            return 1.0;
        }

        float penumbra = (((blockerGap / blockers) * slope) / texelWorld);
        float filterTexels = clamp(penumbra, 1.0, SdfMeshShadowMaxTexels);
        float lit = 0.0;

        [loop]
        for (uint tap = 0u; (tap < SdfMeshShadowFilterTaps); tap++) {
            float r = sqrt((float(tap) + pattern.y) / float(SdfMeshShadowFilterTaps));
            float angle = ((float(tap) * SdfGoldenAngle) + rotation);
            lit += ((sdfMeshShadowGap(cascade, c, texel, (float2(cos(angle), sin(angle)) * (r * filterTexels)), receiver, gradient, texelWorld, SdfMeshShadowSlopeBias, cone) > 0.0) ? 0.0 : 1.0);
        }

        return (lit / float(SdfMeshShadowFilterTaps));
    }

    return 1.0;
}

void sdfSurfaceStage(SdfPixel p) {
    if (!p.active) {
        return;
    }

    SdfMeshSample meshHit = sdfMeshSampleAt(p.pixel);
    bool hit = (meshHit.covered && (meshHit.t < p.farDistance));
    SdfVisibilityRecordValue value;
    value.visibility.t = (hit ? meshHit.t : p.farDistance);
    value.visibility.identity = (hit ? sdfVisibilityIdentity(SDF_VISIBILITY_KIND_MESH, meshHit.draw) : 0u);
    value.visibility.material = (hit ? sdfMeshMaterial(meshHit.draw, meshHit.triangleIndex) : 0);
    value.triangleIndex = (hit ? meshHit.triangleIndex : 0u);
    value.frameSlot = (hit ? asint(sdfMeshRegion[sdfMeshRecord(meshHit.draw) + SdfMeshFrameSlotWord]) : SDF_TRANSFORM_SLOT_NONE);
    value.normal = float3(0.0, 0.0, 0.0);
    value.curvature = 0.0;
    value.ambient = 1.0;
    value.shadows = float4(1.0, 1.0, 1.0, 1.0);

    if (hit) {
        float3 surfacePoint = (p.rayOrigin + (p.rayDirection * meshHit.t));
        float3 face;
        float3 normal = sdfMeshSurfaceNormal(meshHit.draw, meshHit.triangleIndex, surfacePoint, p.rayDirection, face);

        value.normal = normal;
        if (!worldAoDisabled()) {
            value.ambient = sdfMeshAmbient(p, surfacePoint, normal, meshHit.t);
        }
        if (!worldSoftShadowsDisabled() && (passGroup.meshShadowCount > 0u)) {
            // The cascades look along the key light; a surface facing away from it is in its own shadow, which the
            // light's cosine already darkens, so only a lit side queries them.
            float4 parameters = passGroup.meshShadowParameters;
            float3 key = -sdfMeshCascadeAt(0u).forward;
            value.shadows.x = ((dot(normal, key) > 0.0) ? sdfMeshShadow(p, surfacePoint, face, 0u, passGroup.meshShadowCount, parameters.x) : 1.0);
            // The second shadow slot's light, from its one cascade.
            if (parameters.w > 0.0) {
                float3 fill = -sdfMeshCascadeAt(2u).forward;
                value.shadows.y = ((dot(normal, fill) > 0.0) ? sdfMeshShadow(p, surfacePoint, face, 2u, 3u, parameters.w) : 1.0);
            }
        }
    }

    sdfStoreVisibilityRecord(worldVisibilityRecord(p.pixel, p.viewIndex), value);
}

#endif
#endif
