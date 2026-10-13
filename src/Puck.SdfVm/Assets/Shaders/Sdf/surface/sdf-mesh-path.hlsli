// The baked-mesh path's surface stage (SDF_MESH_SURFACE_PASS): one pass in place of primary, surface, ambient and
// shadow, which reads no field. From the mesh target the mesh pass rasterized it writes the whole visibility record a
// mesh pixel carries (V, C and L as primary writes them, N and S as surface writes them), the screen-space ambient
// occlusion into S, and the key light's visibility from the shadow cascades into K's first slot. Views then shades the
// record as it shades any mesh pixel; the pass block's renderPath tells it the K row is current for mesh pixels.
#ifndef SURFACE_SDF_MESH_PATH_HLSLI
#define SURFACE_SDF_MESH_PATH_HLSLI
#ifdef SDF_MESH_SURFACE_PASS

// The screen-space occlusion's world radius, sample count and the depth band past which a closer surface no longer
// occludes.
static const float SdfMeshAoRadius = 0.75;
static const uint SdfMeshAoSamples = 12u;
static const float SdfMeshAoStrength = 1.15;
// The shadow filter's taps: a blocker search and the percentage-closer filter, each over a rotated Vogel disk.
static const uint SdfMeshShadowSearchTaps = 12u;
static const uint SdfMeshShadowFilterTaps = 16u;
static const float SdfMeshShadowMaxTexels = 14.0;
static const float SdfGoldenAngle = 2.39996323;
// The texels along each side of a cascade target. KEEP IN SYNC with SdfWorldPackage.MeshShadowResolution.
#define SDF_MESH_SHADOW_RESOLUTION 2048

// Interleaved gradient noise, rotated each temporal sample, so the resolve averages the disk's rotations.
float sdfMeshNoise(uint2 pixel) {
    float2 shifted = (float2(pixel) + (5.588238 * float(passGroup.historyFrames & 7u)));
    return frac(52.9829189 * frac(dot(shifted, float2(0.06711056, 0.00583715))));
}
// The pixel a world point projects to in the view, and its distance from the camera; false behind the camera.
bool sdfMeshProject(ViewportData view, float3 world, out float2 pixel, out float distance) {
    float3 relative = (world - view.position.xyz);
    float forward = dot(relative, view.forward.xyz);
    pixel = float2(0.0, 0.0);
    distance = length(relative);
    if (forward <= 1.0e-4) {
        return false;
    }
    float2 tangent = (float2(dot(relative, view.right.xyz), dot(relative, view.up.xyz)) / forward) - view.lens.yz;
    float2 ndc = (tangent / float2((view.up.w * view.right.w), view.right.w));
    pixel = (float2(((ndc.x * 0.5) + 0.5), (0.5 - (ndc.y * 0.5))) * view.extent.xy);
    return true;
}
// Normal-oriented hemisphere occlusion against the mesh target's ray distances: a sample point occludes when the surface
// the camera sees through its pixel lies in front of it, by no more than the radius.
float sdfMeshAmbient(SdfPixel p, float3 surfacePoint, float3 normal, float t) {
    float3 helper = ((abs(normal.y) < 0.99) ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0));
    float3 tangent = normalize(cross(helper, normal));
    float3 bitangent = cross(normal, tangent);
    float noise = sdfMeshNoise(p.pixel);
    float radius = min(SdfMeshAoRadius, (0.25 * t));
    float occlusion = 0.0;
    float2 extent = p.view.extent.xy;

    [loop]
    for (uint index = 0u; (index < SdfMeshAoSamples); index++) {
        float fraction = ((float(index) + 0.5) / float(SdfMeshAoSamples));
        float angle = ((float(index) * SdfGoldenAngle) + (noise * 6.2831853));
        float spread = sqrt(fraction);
        float lift = sqrt(max(0.0, (1.0 - fraction)));
        float scale = lerp(0.2, 1.0, frac((noise * 7.0) + (fraction * 3.0)));
        float3 direction = (((tangent * (cos(angle) * spread)) + (bitangent * (sin(angle) * spread))) + (normal * lift));
        float3 sample = ((surfacePoint + (normal * (0.02 * radius))) + (direction * (radius * scale)));
        float2 pixel;
        float distance;

        if (!sdfMeshProject(p.view, sample, pixel, distance) || any(pixel < 0.0) || any(pixel >= extent)) {
            continue;
        }

        float4 texel = meshVisibility.Load(int3(int2(pixel), 0));

        if (texel.y < 1.0) {
            continue;
        }

        float depth = (distance - texel.x);

        if (depth > (0.02 * radius)) {
            occlusion += saturate(1.0 - ((depth - radius) / radius));
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
// The light-space depth of the nearest caster at a cascade texel; past the far plane where the cascade drew nothing.
float sdfMeshShadowDepth(uint cascade, int2 texel, float far) {
    int2 clamped = clamp(texel, int2(0, 0), int2(SDF_MESH_SHADOW_RESOLUTION - 1, SDF_MESH_SHADOW_RESOLUTION - 1));
    float4 value = ((cascade == 0u) ? meshShadowNear.Load(int3(clamped, 0)) : meshShadowFar.Load(int3(clamped, 0)));
    return ((value.y >= 1.0) ? value.x : far);
}
// The key light's visibility at a surface point from the first cascade that holds it: a blocker search sizes the
// penumbra from the light's angular size and the gap to the average blocker, and a percentage-closer filter of that
// width answers.
float sdfMeshShadow(SdfPixel p, float3 surfacePoint, float3 normal) {
    float noise = (sdfMeshNoise(p.pixel) * 6.2831853);
    float4 parameters = passGroup.meshShadowParameters;

    [loop]
    for (uint cascade = 0u; (cascade < passGroup.meshShadowCount); cascade++) {
        SdfMeshCascade c = sdfMeshCascadeAt(cascade);
        float texelWorld = ((2.0 * c.halfHeight) / float(SDF_MESH_SHADOW_RESOLUTION));
        float3 biased = (surfacePoint + (normal * (parameters.z * texelWorld)));
        float3 relative = (biased - c.position);
        float2 plane = float2((dot(relative, c.right) / (c.aspect * c.halfHeight)), (dot(relative, c.up) / c.halfHeight));

        if (any(abs(plane) > 0.97)) {
            continue;
        }

        float2 texel = (float2(((plane.x * 0.5) + 0.5), (0.5 - (plane.y * 0.5))) * float(SDF_MESH_SHADOW_RESOLUTION));
        float receiver = (dot(relative, c.forward) - (parameters.y + texelWorld));
        float searchTexels = SdfMeshShadowMaxTexels;
        float blockers = 0.0;
        float blockerDepth = 0.0;

        [loop]
        for (uint tap = 0u; (tap < SdfMeshShadowSearchTaps); tap++) {
            float r = sqrt((float(tap) + 0.5) / float(SdfMeshShadowSearchTaps));
            float angle = ((float(tap) * SdfGoldenAngle) + noise);
            float depth = sdfMeshShadowDepth(cascade, int2(texel + (float2(cos(angle), sin(angle)) * (r * searchTexels))), c.far);
            if (depth < receiver) {
                blockers += 1.0;
                blockerDepth += depth;
            }
        }

        if (blockers <= 0.0) {
            return 1.0;
        }

        blockerDepth /= blockers;

        float penumbra = (((receiver - blockerDepth) * parameters.x) / texelWorld);
        float filterTexels = clamp(penumbra, 1.0, SdfMeshShadowMaxTexels);
        float lit = 0.0;

        [loop]
        for (uint tap = 0u; (tap < SdfMeshShadowFilterTaps); tap++) {
            float r = sqrt((float(tap) + 0.5) / float(SdfMeshShadowFilterTaps));
            float angle = ((float(tap) * SdfGoldenAngle) + noise);
            float depth = sdfMeshShadowDepth(cascade, int2(texel + (float2(cos(angle), sin(angle)) * (r * filterTexels))), c.far);
            lit += ((depth >= receiver) ? 1.0 : 0.0);
        }

        return (lit / float(SdfMeshShadowFilterTaps));
    }

    return 1.0;
}

void sdfMeshPathStage(SdfPixel p) {
    if (!p.active) {
        return;
    }

    uint record = worldVisibilityRecord(p.pixel, p.viewIndex);
    SdfMeshSample meshHit = sdfMeshSampleAt(p.pixel);
    bool hit = (meshHit.covered && (meshHit.t < p.farDistance));
    SdfVisibility visibility;
    visibility.t = (hit ? meshHit.t : p.farDistance);
    visibility.identity = (hit ? sdfVisibilityIdentity(SDF_VISIBILITY_KIND_MESH, meshHit.draw) : 0u);
    visibility.material = (hit ? sdfMeshMaterial(meshHit.draw, meshHit.triangleIndex) : 0);
    visibility.flags = 0u;
    SdfVisibilityCoverage coverage;
    coverage.terminalRadius = 0.0;
    coverage.threshold = 1.0;
    coverage.blendWeight = 0.0;
    coverage.blendOther = 0;
    sdfStoreVisibility(record, visibility);
    sdfVisibilityStoreWord(record + SdfVisibilityRowI + 7u, 0u);
    sdfStoreVisibilityCoverage(record, coverage);
    sdfStoreVisibilityMeshTriangle(record, (hit ? meshHit.triangleIndex : 0u));
    // The ambient and shadow passes' query tallies: no query reads the field here.
    sdfVisibilityStoreWord(record + SDF_VISIBILITY_AMBIENT_QUERIES_WORD, 0u);
    sdfVisibilityStoreWord(record + SDF_VISIBILITY_SHADOW_QUERIES_WORD, 0u);

    float3 normal = float3(0.0, 0.0, 0.0);
    float ambient = 1.0;
    float shadow = 1.0;

    if (hit) {
        float3 surfacePoint = (p.rayOrigin + (p.rayDirection * meshHit.t));

        normal = sdfMeshSurfaceNormal(meshHit.draw, meshHit.triangleIndex, surfacePoint, p.rayDirection);
        if (!worldAoDisabled()) {
            ambient = sdfMeshAmbient(p, surfacePoint, normal, meshHit.t);
        }
        if (!worldSoftShadowsDisabled() && (passGroup.meshShadowCount > 0u)) {
            // The cascades look along the key light; a surface facing away from it is in its own shadow, which the
            // light's cosine already darkens, so only a lit side queries them.
            float3 key = -sdfMeshCascadeAt(0u).forward;
            shadow = ((dot(normal, key) > 0.0) ? sdfMeshShadow(p, surfacePoint, normal) : 1.0);
        }    }

    SdfVisibilityNormal geometric;
    geometric.normal = normal;
    geometric.gradientMagnitude = 1.0;
    SdfVisibilitySurface surface;
    surface.curvature = 0.0;
    surface.queries = 0.0;
    surface.ambient = ambient;
    surface.flags = 0u;
    sdfStoreVisibilityNormal(record, geometric);
    sdfStoreVisibilitySurface(record, surface);
    sdfStoreVisibilityShadows(record, float4(shadow, 1.0, 1.0, 1.0));
}

#endif
#endif
