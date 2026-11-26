// The reflection pass (SdfWorldPackage.Parts.ViewsReflections): one invocation a cell of passGroup.screenReflections
// render pixels a side, in 8x8 groups, run before views in a temporal view (SdfWorldPackage.WithReflections). Each cell
// traces one mirror ray this frame, from its representative pixel (sdfViewsReflectionPhase), which turns through the
// cell's pixels frame by frame; views blends each pixel's nearest cells whose representatives lie on its surface
// (sdfViewsScreenReflection). A glossy representative's mirror ray, about its record's normal, is projected onto the
// screen, clipped to the screen and the near plane, and marched through the reflection pyramid
// (frame/sdf-reflection-depth.hlsli): its inverse depth is linear along the projection, so a cell of any level the ray
// crosses in front of the nearest surface it holds is skipped whole and the march climbs a level, and a cell it reaches
// is entered a level finer, down to the mesh target's own pixels. What the ray reaches is read from the preceding frame's
// history colour where the hit stood then (sdfPreviousPoint) and the history shows it (sdfReflectHistoryShows), widened by
// the specular lobe's footprint there, with the air's in-scatter the composite added to that pixel and the history does
// not hold; a ray that passes in front of everything it crosses and ends over a pixel no mesh covers answers the open sky.
// Views blends the records over the irradiance volume's reflection, which still answers whatever leaves the screen, hides
// behind a nearer face, or belongs to a rough surface.
#define SDF_VIEWS_REFLECTIONS_PASS
#include "sdf-pixel-pass.hlsli"
#include "sdf-views-reflections.hlsli"
#include "../frame/sdf-reprojection.hlsli"
#include "../frame/sdf-history-surface.hlsli"
#include "../frame/sdf-reflection-depth.hlsli"
#include "../shade/sdf-transport.hlsli"
#include "Shared/luma.hlsli"

// The roughness up to which the screen answers a surface's whole reflection, and from which the volume answers it alone.
static const float SdfReflectSharp = 0.3;
static const float SdfReflectRough = 0.55;
// The least share of the light arriving from every side that a surface's reflection of the screen returns (its glossiness
// times the luminance of its specular lobe's directional albedo, sdfMaterialEnvironmentBrdf) for its pixel to trace: below
// it the screen's answer moves the pixel by less than one step of an eight-bit display however bright what it reflects.
static const float SdfReflectLeast = (1.0 / 256.0);
// How far behind its visible face an occluder is taken to reach, in metres and as a share of its forward depth: a ray
// that passes farther behind it passes behind the occluder.
static const float SdfReflectThickness = 0.15;
static const float SdfReflectThicknessShare = 0.03;
// The most pixels in a row a ray walks behind occluders before it gives up: past a thin occluder (a rail, a leg) it walks
// on to what lies behind, while behind a broad one it would take a step a pixel to the budget's end and still find what
// the screen does not show.
static const uint SdfReflectBehindPixels = 4u;
// How far past a cell's edge, in render pixels, a step lands, so the next step reads the cell beyond it.
static const float SdfReflectNudge = 0.005;
// The share of the screen's shorter side over which a hit fades toward the screen's edge.
static const float SdfReflectEdge = 0.08;
// The history taps that spread a hit over the lobe's footprint, turned each jitter phase, and the widest footprint radius,
// in history pixels.
static const uint SdfReflectTaps = 4u;
static const float SdfReflectRadius = 24.0;
// The golden angle, which turns each tap of the footprint's spiral and the spiral itself from one jitter phase to the next.
static const float SdfReflectGoldenAngle = 2.39996323;

// Whether the preceding frame's history shows a hit's surface about a position in its pixels: any of the four history
// pixels about it holds the hit's receiver at its previous distance (sdfHistoryReceiverMatches, the resolve's own test),
// so a hit the preceding frame saw hidden behind another surface reads nothing, while one at a silhouette still reads.
bool sdfReflectHistoryShows(float2 position, uint2 extent, uint identity, float previousT) {
    int2 corner = (int2)floor(position - 0.5);
    bool shows = false;

    [unroll]
    for (uint tap = 0u; tap < 4u; tap++) {
        uint2 texel = (uint2)clamp((corner + int2((int)(tap & 1u), (int)(tap >> 1u))), int2(0, 0), (int2(extent) - 1));
        uint pixel = ((texel.y * extent.x) + texel.x);

        shows = (shows || sdfHistoryReceiverMatches(identity, historySurface[sdfHistoryWord(pixel, 0u, extent)], previousT,
            f16tof32(historySurface[sdfHistoryWord(pixel, 1u, extent)] & 0xFFFFu)));
    }
    return shows;
}
// The air's in-scatter the composite added to a history pixel and its colour does not hold (sdf-transport.hlsli), in nits,
// premultiplied by the pixel's coverage as its colour is: its accumulated transport's weights (the history surface's
// second and third words) times each kind's colour along the preceding camera's ray to it.
float3 sdfReflectInScatter(uint pixel, uint2 extent, float3 direction) {
    float4 transport = sdfUnpackTransport(uint2(historySurface[sdfHistoryWord(pixel, 2u, extent)], historySurface[sdfHistoryWord(pixel, 3u, extent)]));
    float3 weights = (transport.xzw * (1.0 - saturate(passGroup.screenFidelity)));

    if (all(weights <= 0.0)) {
        return float3(0.0, 0.0, 0.0);
    }

    float airLight = saturate(passGroup.airLight);
    float3 ambient = (sdfAirInScatterReadsSky(weights) ? (sdfSkyPhysicalRadiance(direction) * airLight) : float3(0.0, 0.0, 0.0));

    return (((sdfAirFogColor(ambient) * weights.x) + (sdfAirHazeColor(ambient, direction, airLight) * weights.y)) + (sdfSky[0].MediumColor * weights.z));
}

// Marches a ray's projected segment, from `start` along `delta` in render pixels with inverse forward depth
// `startInverse` there and `finishInverse` at its end, from where it leaves its own pixel, for at most `budget` reads of
// the pyramid, and returns the share of the segment at which it meets a surface, or -1 for none. `open` says whether the
// march passed in front of everything to the segment's end.
float sdfReflectMarch(float2 start, float2 delta, float startInverse, float finishInverse, uint budget, out bool open) {
    uint2 dims = passGroup.imageExtent;
    SdfReflectDepthBases bases = sdfReflectDepthBases(dims);
    float2 inverse = float2(((delta.x != 0.0) ? (1.0 / delta.x) : 1.0e30), ((delta.y != 0.0) ? (1.0 / delta.y) : 1.0e30));
    uint2 toward = uint2((delta.x >= 0.0) ? 1u : 0u, (delta.y >= 0.0) ? 1u : 0u);
    float2 nudge = select((delta >= 0.0), SdfReflectNudge.xx, -SdfReflectNudge.xx);
    float rate = (finishInverse - startInverse);
    float2 leaves = ((floor(start) + float2(toward) + nudge - start) * inverse);
    float share = max(min(leaves.x, leaves.y), 0.0);
    uint level = 0u;
    uint behind = 0u;

    open = false;

    [loop]
    for (uint step = 0u; ((step < budget) && (share < 1.0)); step++) {
        uint2 pixel = (uint2)clamp((start + (delta * share)), float2(0.0, 0.0), (float2(dims) - 1.0));
        uint2 texel = (pixel >> level);
        float cellInverse = ((level == 0u)
            ? sdfReflectDepthPixel(pixel)
            : asfloat(sdfReflectionDepth[sdfReflectDepthWord(sdfReflectDepthBaseOf(bases, level), dims, level, texel)]));
        float rayInverse = (startInverse + (rate * share));
        float2 edges = ((float2((texel + toward) << level) + nudge - start) * inverse);
        float leave = min(edges.x, edges.y);

        sdfWorkSteps++;
        if (rayInverse > cellInverse) {
            // In front of everything the cell holds: the ray leaves it, unless it falls behind the cell's nearest surface
            // first, where it enters the cell a level finer.
            behind = 0u;
            float meets = (((rate < 0.0) && (cellInverse > 0.0)) ? ((cellInverse - startInverse) / rate) : 2.0);

            if ((meets < leave) && (meets <= 1.0)) {
                share = max(share, meets);
                if (level == 0u) {
                    return share;
                }
                level--;
            } else {
                share = leave;
                level = min((level + 1u), SDF_REFLECTION_DEPTH_LEVELS);
            }
        } else if (level > 0u) {
            level--;
        } else {
            // Behind a pixel's surface: within the occluder's thickness the ray meets it; farther behind, it passes behind.
            float surfaceDepth = (1.0 / cellInverse);

            if (((1.0 / rayInverse) - surfaceDepth) <= (SdfReflectThickness + (SdfReflectThicknessShare * surfaceDepth))) {
                return share;
            }
            if (++behind > SdfReflectBehindPixels) {
                return -1.0;
            }
            share = leave;
        }
    }
    open = (share >= 1.0);
    return -1.0;
}

// The reflection record of one cell, traced from its representative pixel (passes/sdf-views-reflections.hlsli).
uint4 sdfReflectTrace(SdfPixel p) {
    uint4 none = uint4(0u, 0u, SDF_VISIBILITY_ZERO_NORMAL, 0u);

    // A history of another epoch holds light at another scale and from another camera; a debug image is no picture.
    if ((passGroup.historyFrames == 0u) || (passGroup.previousView[0].w == 0.0) || !sdfDebugComposesFinal(passGroup.debugMode)) {
        return none;
    }

    uint record = worldVisibilityRecord(p.pixel, p.viewIndex);
    SdfVisibility visibility = sdfLoadVisibility(record);

    if (!sdfVisibilityHit(visibility) || (visibility.material >= SDF_SCREEN_MATERIAL)) {
        return none;
    }

    SdfMaterialData material = sdfMaterialLoad(visibility.material);

    if ((material.specular <= 0.0) && (material.metal <= 0.0)) {
        return none;
    }

    uint normalWord = sdfVisibilityRecordBuffer[record + SdfVisibilityNormalWord];
    float3 normal = sdfVisibilityUnpackNormal(normalWord);
    float3 direction = reflect(p.rayDirection, normal);
    float glossy = (1.0 - smoothstep(SdfReflectSharp, SdfReflectRough, material.roughness));
    float3 f0 = lerp(material.specular.xxx, material.albedo, material.metal);
    float returned = (glossy * dot(saturate(sdfMaterialEnvironmentBrdf(f0, material.roughness, saturate(dot(normal, -p.rayDirection)))), PuckRec709Luminance));

    if ((returned < SdfReflectLeast) || (dot(direction, normal) <= 0.0)) {
        return none;
    }
    sdfWorkTexels = 1u;

    float4 rows[6];

    sdfCurrentViewRows(rows);

    float2 dims = float2(passGroup.imageExtent);
    float3 surface = sdfPixelSurfacePoint(p, visibility.t);
    // The representative's tangent plane, relative to the eye, which a views pixel tests its own surface against.
    float plane = dot((surface - rows[0].xyz), normal);
    // A record that traced and found nothing: the pixels on the representative's surface take the volume's reflection.
    uint4 traced = uint4(0u, 0u, normalWord, asuint(plane));
    // The march starts a footprint off the surface, relative to the eye, so the pixel's own face never hides the ray.
    float3 origin = ((surface - rows[0].xyz) + (normal * (p.pixelFootprint * visibility.t)));
    float nearDepth = worldSurfaceNearDistance(p.view);
    float startDepth = dot(origin, rows[3].xyz);
    float depthRate = dot(direction, rows[3].xyz);
    bool towardEye = (depthRate < 0.0);
    float reach = (towardEye ? min(p.farDistance, (((startDepth - nearDepth) / -depthRate) * 0.99)) : p.farDistance);
    float2 start;
    float2 finish;

    if ((startDepth <= nearDepth) || (reach <= 0.0) ||
        !sdfProjectView(rows, origin, start) || !sdfProjectView(rows, (origin + (direction * reach)), finish)) {
        return traced;
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

    if (max(abs(delta.x), abs(delta.y)) < 1.0) {
        return traced;
    }

    bool open;
    float hitShare = sdfReflectMarch(start, delta, startInverse, finishInverse, passGroup.reflectionSteps, open);

    if (hitShare < 0.0) {
        // A ray that passed in front of everything it crossed and ended over open sky, away from the eye, sees the sky.
        return ((open && !towardEye && (sdfReflectDepthPixel((uint2)clamp((start + delta), float2(0.0, 0.0), (dims - 1.0))) <= 0.0))
            ? sdfViewsReflectionPack(float3(0.0, 0.0, 0.0), 0.0, saturate(glossy), normalWord, plane) : traced);
    }

    float2 at = (start + (delta * hitShare));
    uint2 hitPixel = (uint2)clamp(at, float2(0.0, 0.0), (dims - 1.0));
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
        return traced;
    }

    float2 uv = (previousPixel / passGroup.previousView[4].xy);
    // The history's planes follow the output extent, which the recorder writes for a pass that reads them.
    uint2 historyDims = passGroup.viewsRate.zw;
    float3 previousRay = (previousPoint - passGroup.previousView[0].xyz);
    float previousT = length(previousRay);

    if (any(uv < 0.0) || any(uv > 1.0) || any(historyDims == 0u) ||
        !sdfReflectHistoryShows((uv * float2(historyDims)), historyDims, hitVisibility.identity, previousT)) {
        return traced;
    }

    // The lobe's footprint where the ray lands, seen from the preceding camera: GGX's half-width about the mirror
    // direction is near twice its alpha (the roughness squared), and a history pixel spans the preceding view's pixel
    // footprint at the hit's distance from it.
    float alpha = (material.roughness * material.roughness);
    float travelled = length((hitPoint - rows[0].xyz) - origin);
    float pixelMetres = (((2.0 * passGroup.previousView[1].w) / float(historyDims.y)) * previousT);
    float radius = min(((2.0 * alpha * travelled) / max(pixelMetres, 1.0e-6)), SdfReflectRadius);
    float4 sum = float4(0.0, 0.0, 0.0, 0.0);
    // SDF_VIEWS_REFLECTIONS_ONE_TAP, defined for a reload, reads the footprint's centre alone, so the taps' share of the
    // pass's time reads apart from the march's.
#if defined(SDF_VIEWS_REFLECTIONS_ONE_TAP)
    uint taps = 1u;
    radius = 0.0;
#else
    uint taps = ((radius < 0.5) ? 1u : SdfReflectTaps);
#endif
    float turn = (SdfReflectGoldenAngle * float(passGroup.historyFrames % SDF_TEMPORAL_PERIOD));

    [loop]
    for (uint tap = 0u; tap < taps; tap++) {
        float along = (radius * sqrt((float(tap) + 0.5) / float(taps)));
        float angle = ((SdfReflectGoldenAngle * float(tap)) + turn);
        float2 offset = ((along * float2(cos(angle), sin(angle))) / float2(historyDims));

        sum += sdfReflectionHistory.SampleLevel(samplers[SDF_FILTER_LINEAR], saturate(uv + offset), 0.0);
    }
    sum /= float(taps);

    // The air between the preceding camera and the hit, which the composite added after the history was written.
    uint2 centre = min((uint2)(uv * float2(historyDims)), (historyDims - 1u));

    sum.rgb += (sdfReflectInScatter(((centre.y * historyDims.x) + centre.x), historyDims, (previousRay / max(previousT, 1.0e-6)))
        * sdfStoredLightScale(passGroup.debugMode, passGroup.lightScale));

    // The history is premultiplied by coverage: what of the footprint no surface covered is the open sky beyond it.
    float2 edge = (min(at, (dims - at)) / (SdfReflectEdge * min(dims.x, dims.y)));
    float confidence = (glossy * smoothstep(0.0, 1.0, saturate(min(edge.x, edge.y))));
    float coverage = saturate(sum.a);

    if (!all(isfinite(sum)) || (confidence <= 0.0)) {
        return traced;
    }
    return sdfViewsReflectionPack((sum.rgb / max(sum.a, 1.0e-4)), (confidence * coverage), (confidence * (1.0 - coverage)), normalWord, plane);
}

[numthreads(SDF_VISIBILITY_GROUP_EDGE, SDF_VISIBILITY_GROUP_EDGE, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint viewIndex = worldViewOf(id.z);
    uint cellEdge = max(passGroup.screenReflections, 1u);
    uint2 dims = passGroup.imageExtent;

    if (viewIndex >= passGroup.viewportCount) {
        return;
    }
    // Every lane reaches the group's count below; only a lane over a cell of the extent traces.
    if (all((id.xy * cellEdge) < dims)) {
        // The cell's representative this frame, or its last pixel along an axis the extent cuts short.
        uint2 pixel = min(((id.xy * cellEdge) + sdfViewsReflectionPhase(passGroup.historyFrames, cellEdge)), (dims - 1u));

        sdfViewsReflectionsRW[sdfViewsReflectionCell(id.xy, viewIndex, cellEdge)] = sdfReflectTrace(sdfPixelAt(worldView(), pixel, viewIndex));
    }
    // A cell whose mirror ray it marched counts as a texel, and each read of the pyramid as a step.
    puckCountWork(sdfWorkSteps, sdfWorkTexels);
}
