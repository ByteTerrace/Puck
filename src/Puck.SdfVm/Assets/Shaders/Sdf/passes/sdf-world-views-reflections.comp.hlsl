// The reflection pass (SdfWorldPackage.Parts.ViewsReflections): one invocation a pixel of the render extent, in 8x8
// groups, run before views in a temporal view (SdfWorldPackage.WithReflections). A glossy pixel's mirror ray, about its
// record's normal, is marched across the screen through the mesh target's forward depth: perspective-correct, the ray's
// inverse depth interpolated along its projected segment, which is clipped to the screen and to the near plane, and a
// crossing found between two samples is narrowed by bisection. What the ray reaches is read from the preceding frame's
// history colour where the hit stood then (sdfPreviousPoint), widened by the specular lobe's footprint there; a ray that
// passes in front of everything it crosses and ends over a pixel no mesh covers answers the open sky. Views blends the
// record over the irradiance volume's reflection (sdfViewsScreenReflection), which still answers whatever leaves the
// screen, hides behind a nearer face, or belongs to a rough surface.
#define SDF_VIEWS_REFLECTIONS_PASS
#include "sdf-pixel-pass.hlsli"
#include "sdf-views-reflections.hlsli"
#include "../frame/sdf-reprojection.hlsli"

// The roughness up to which the screen answers a surface's whole reflection, and from which the volume answers it alone.
static const float SdfReflectSharp = 0.3;
static const float SdfReflectRough = 0.55;
// The samples a ray takes along its projected segment at most, and the bisections that narrow a crossing.
static const uint SdfReflectSteps = 32u;
static const uint SdfReflectBisections = 5u;
// The pixels from the ray's start of its first sample, past the pixel's own face.
static const float SdfReflectFirstPixels = 1.5;
// How far behind its visible face an occluder is taken to reach, in metres and as a share of its forward depth: a ray
// that passes farther behind it passes behind the occluder.
static const float SdfReflectThickness = 0.15;
static const float SdfReflectThicknessShare = 0.03;
// The share of the screen's shorter side over which a hit fades toward the screen's edge.
static const float SdfReflectEdge = 0.08;
// The history taps that spread a hit over the lobe's footprint, and the widest footprint radius, in history pixels.
static const uint SdfReflectTaps = 8u;
static const float SdfReflectRadius = 24.0;
// The golden angle, which turns each tap of the footprint's spiral and the spiral itself from one jitter phase to the next.
static const float SdfReflectGoldenAngle = 2.39996323;

// The mesh target's forward depth at a render pixel, and whether a mesh covers it.
float sdfReflectDepth(float2 at, out bool covered) {
    float4 texel = meshVisibility.Load(int3(int2(at), 0));

    covered = (texel.y > 0.0);
    return texel.w;
}

// The reflection record of one pixel (passes/sdf-views-reflections.hlsli).
uint2 sdfReflectTrace(SdfPixel p) {
    // A history of another epoch holds light at another scale and from another camera; a debug image is no picture.
    if ((passGroup.historyFrames == 0u) || (passGroup.previousView[0].w == 0.0) || !sdfDebugComposesFinal(passGroup.debugMode)) {
        return uint2(0u, 0u);
    }

    uint record = worldVisibilityRecord(p.pixel, p.viewIndex);
    SdfVisibility visibility = sdfLoadVisibility(record);

    if (!sdfVisibilityHit(visibility) || (visibility.material >= SDF_SCREEN_MATERIAL)) {
        return uint2(0u, 0u);
    }

    SdfMaterialData material = sdfMaterialLoad(visibility.material);
    float glossy = (((material.specular > 0.0) || (material.metal > 0.0)) ? (1.0 - smoothstep(SdfReflectSharp, SdfReflectRough, material.roughness)) : 0.0);
    float3 normal = sdfVisibilityUnpackNormal(sdfVisibilityRecordBuffer[record + SdfVisibilityNormalWord]);
    float3 direction = reflect(p.rayDirection, normal);

    if ((glossy <= 0.0) || (dot(direction, normal) <= 0.0)) {
        return uint2(0u, 0u);
    }
    sdfWorkTexels = 1u;

    float4 rows[6];

    sdfCurrentViewRows(rows);

    float2 dims = float2(passGroup.imageExtent);
    // The march starts a footprint off the surface, relative to the eye, so the pixel's own face never hides the ray.
    float3 origin = ((sdfPixelSurfacePoint(p, visibility.t) - rows[0].xyz) + (normal * (p.pixelFootprint * visibility.t)));
    float nearDepth = worldSurfaceNearDistance(p.view);
    float startDepth = dot(origin, rows[3].xyz);
    float depthRate = dot(direction, rows[3].xyz);
    bool towardEye = (depthRate < 0.0);
    float reach = (towardEye ? min(p.farDistance, (((startDepth - nearDepth) / -depthRate) * 0.99)) : p.farDistance);
    float2 start;
    float2 finish;

    if ((startDepth <= nearDepth) || (reach <= 0.0) ||
        !sdfProjectView(rows, origin, start) || !sdfProjectView(rows, (origin + (direction * reach)), finish)) {
        return uint2(0u, 0u);
    }
    // The render grid's samples lie where the jittered rays met the scene.
    start -= passGroup.jitter;
    finish -= passGroup.jitter;

    float startInverse = (1.0 / startDepth);
    float finishInverse = (1.0 / dot((origin + (direction * reach)), rows[3].xyz));
    float2 delta = (finish - start);
    float clip = 1.0;

    // The share of the segment before it leaves the screen, whose pixel centres run from 0.5 to the extent less 0.5.
    clip = ((delta.x > 0.0) ? min(clip, (((dims.x - 0.5) - start.x) / delta.x)) : ((delta.x < 0.0) ? min(clip, ((0.5 - start.x) / delta.x)) : clip));
    clip = ((delta.y > 0.0) ? min(clip, (((dims.y - 0.5) - start.y) / delta.y)) : ((delta.y < 0.0) ? min(clip, ((0.5 - start.y) / delta.y)) : clip));
    clip = max(clip, 0.0);
    delta *= clip;
    finishInverse = lerp(startInverse, finishInverse, clip);

    float span = max(abs(delta.x), abs(delta.y));

    if (span < (2.0 * SdfReflectFirstPixels)) {
        return uint2(0u, 0u);
    }

    uint steps = min(SdfReflectSteps, (uint)span);
    float first = (SdfReflectFirstPixels / span);
    // Interleaved gradient noise, turned each jitter phase, so a stationary view's window averages the samples' offsets.
    float noise = frac((52.9829189 * frac(dot(float2(p.pixel), float2(0.06711056, 0.00583715)))) + (0.618034 * float(passGroup.historyFrames % SDF_TEMPORAL_PERIOD)));
    float previousShare = 0.0;
    float previousDepth = startDepth;
    float hitShare = -1.0;
    bool lastCovered = true;

    [loop]
    for (uint index = 1u; index <= steps; index++) {
        float share = max(first, ((float(index) - noise) / float(steps)));
        float rayDepth = (1.0 / lerp(startInverse, finishInverse, share));
        bool covered;
        float sceneDepth = sdfReflectDepth((start + (delta * share)), covered);

        sdfWorkSteps++;
        lastCovered = covered;
        if (covered && (max(previousDepth, rayDepth) >= sceneDepth) &&
            (min(previousDepth, rayDepth) <= (sceneDepth + (SdfReflectThickness + (SdfReflectThicknessShare * sceneDepth))))) {
            float low = previousShare;
            float high = share;

            [loop]
            for (uint bisection = 0u; bisection < SdfReflectBisections; bisection++) {
                float middle = (0.5 * (low + high));
                bool middleCovered;
                float middleDepth = sdfReflectDepth((start + (delta * middle)), middleCovered);

                sdfWorkSteps++;
                if (middleCovered && ((1.0 / lerp(startInverse, finishInverse, middle)) >= middleDepth)) {
                    high = middle;
                } else {
                    low = middle;
                }
            }
            hitShare = high;
            break;
        }
        previousShare = share;
        previousDepth = rayDepth;
    }

    if (hitShare < 0.0) {
        // A ray that passed in front of everything it crossed and ended over open sky, away from the eye, sees the sky.
        return ((!lastCovered && !towardEye) ? sdfViewsReflectionPack(float3(0.0, 0.0, 0.0), 0.0, glossy) : uint2(0u, 0u));
    }

    float2 at = (start + (delta * hitShare));
    uint2 hitPixel = (uint2)at;
    uint hitRecord = worldVisibilityRecord(hitPixel, p.viewIndex);
    SdfVisibility hitVisibility = sdfLoadVisibility(hitRecord);
    float3 hitNormal = sdfVisibilityUnpackNormal(sdfVisibilityRecordBuffer[hitRecord + SdfVisibilityNormalWord]);
    float3 hitPoint = sdfPixelSurfacePoint(sdfPixelAt(p.view, hitPixel, p.viewIndex), hitVisibility.t);
    float3 previousPoint;
    float2 previousPixel;

    // A face turned from the ray is one the ray passed behind.
    if (!sdfVisibilityHit(hitVisibility) || (dot(hitNormal, direction) > 0.0) ||
        !sdfPreviousPoint(hitRecord, hitVisibility, hitPoint, previousPoint) ||
        !sdfProjectView(passGroup.previousView, (previousPoint - passGroup.previousView[0].xyz), previousPixel)) {
        return uint2(0u, 0u);
    }

    float2 uv = (previousPixel / passGroup.previousView[4].xy);

    if (any(uv < 0.0) || any(uv > 1.0)) {
        return uint2(0u, 0u);
    }

    uint2 historyDims;

    sdfReflectionHistory.GetDimensions(historyDims.x, historyDims.y);

    // The lobe's footprint where the ray lands, seen from the preceding camera: GGX's half-width about the mirror
    // direction is near twice its alpha (the roughness squared), and a history pixel spans the preceding view's pixel
    // footprint at the hit's distance from it.
    float alpha = (material.roughness * material.roughness);
    float travelled = length((hitPoint - rows[0].xyz) - origin);
    float pixelMetres = (((2.0 * passGroup.previousView[1].w) / float(historyDims.y)) * length(previousPoint - passGroup.previousView[0].xyz));
    float radius = min(((2.0 * alpha * travelled) / max(pixelMetres, 1.0e-6)), SdfReflectRadius);
    float4 sum = float4(0.0, 0.0, 0.0, 0.0);
    uint taps = ((radius < 0.5) ? 1u : SdfReflectTaps);
    float turn = (SdfReflectGoldenAngle * float(passGroup.historyFrames % SDF_TEMPORAL_PERIOD));

    [loop]
    for (uint tap = 0u; tap < taps; tap++) {
        float along = (radius * sqrt((float(tap) + 0.5) / float(taps)));
        float angle = ((SdfReflectGoldenAngle * float(tap)) + turn);
        float2 offset = ((along * float2(cos(angle), sin(angle))) / float2(historyDims));

        sum += sdfReflectionHistory.SampleLevel(samplers[SDF_FILTER_LINEAR], saturate(uv + offset), 0.0);
    }
    sum /= float(taps);

    // The history is premultiplied by coverage: what of the footprint no surface covered is the open sky beyond it.
    float2 edge = (min(at, (dims - at)) / (SdfReflectEdge * min(dims.x, dims.y)));
    float confidence = (glossy * smoothstep(0.0, 1.0, saturate(min(edge.x, edge.y))));
    float coverage = saturate(sum.a);

    if (!all(isfinite(sum)) || (confidence <= 0.0)) {
        return uint2(0u, 0u);
    }
    return sdfViewsReflectionPack((sum.rgb / max(sum.a, 1.0e-4)), (confidence * coverage), (confidence * (1.0 - coverage)));
}

[numthreads(SDF_VISIBILITY_GROUP_EDGE, SDF_VISIBILITY_GROUP_EDGE, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint viewIndex = worldViewOf(id.z);

    if (viewIndex >= passGroup.viewportCount) {
        return;
    }

    SdfPixel p = sdfPixelAt(worldView(), id.xy, viewIndex);

    if (p.active) {
        sdfViewsReflectionsRW[sdfReactivityIndex(p.pixel, viewIndex, passGroup.imageExtent)] = sdfReflectTrace(p);
    }
    // A pixel whose mirror ray it marched counts as a texel, and each depth it read as a step.
    puckCountWork(sdfWorkSteps, sdfWorkTexels);
}
