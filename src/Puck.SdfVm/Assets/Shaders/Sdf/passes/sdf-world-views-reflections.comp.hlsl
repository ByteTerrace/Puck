// The reflection pass (SdfWorldPackage.Parts.ViewsReflections), two dispatches the pushed index chooses between, run
// before views in a temporal view (SdfWorldPackage.WithReflections). Step zero prefilters the preceding frame's history
// colour into every level of the reflection colour (frame/sdf-reflection-color.hlsli), a group a 32 by 32 block of it.
// Step one runs one invocation a cell of passGroup.screenReflections render pixels a side, in 8x8 groups. Each cell
// traces one mirror ray this frame, from its representative pixel (sdfViewsReflectionPhase), which turns through the
// cell's pixels frame by frame; views blends each pixel's nearest cells whose representatives lie on its surface
// (sdfViewsScreenReflection). A glossy representative's mirror ray, about its record's normal, is projected onto the
// screen, clipped to the screen and the near plane, and marched through the reflection pyramid
// (frame/sdf-reflection-depth.hlsli): its inverse depth is linear along the projection, so a cell of any level the ray
// crosses in front of the nearest surface it holds is skipped whole and the march climbs a level, and a cell it reaches
// is entered a level finer, down to the mesh target's own pixels, where the ray meets a pixel's surface only by crossing
// its sample's tangent plane from the front inside the pixel, never a face turned from it (sdfReflectMarch). The hit is
// that crossing (sdfReflectCrossing), below the pixel. What the ray reaches is read from the preceding frame's
// history colour where the hit stood then (sdfPreviousPoint) and the history shows it (sdfReflectHistoryShows), read as
// one filtered sample of the prefiltered level the specular lobe's footprint spans there (sdfReflectColorFootprint), so a
// rough surface's blur is the same every frame and adds no noise for the resolve to reject in motion; with it, the air's
// in-scatter the composite added to that pixel and the history does not hold. A ray that passes in front of everything it
// crosses and ends over a pixel no mesh covers answers the open sky, as far as its end stands from the screen's edge. A
// hit's share fades toward the screen's edge, over the last steps of the budget and over the pixels a ray walked behind
// an occluder before it, so where rays stop answering the volume takes over smoothly rather than at a line.
// Views blends the records over the irradiance volume's reflection, which still answers whatever leaves the screen, hides
// behind a nearer face, or belongs to a rough surface.
#define SDF_VIEWS_REFLECTIONS_PASS
#include "sdf-pixel-pass.hlsli"
#include "sdf-views-reflections.hlsli"
#include "../frame/sdf-reprojection.hlsli"
#include "../frame/sdf-history-surface.hlsli"
#include "../frame/sdf-reflection-depth.hlsli"
#include "../frame/sdf-reflection-color.hlsli"
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
// The most render pixels a ray walks behind occluders before it gives up: past a thin occluder (a rail, a leg) it walks
// on to what lies behind, while behind a broad one it would take a step a pixel to the budget's end and still find what
// the screen does not show. A surface met after a walk shares less the farther the walk went (sdfReflectTrace).
static const float SdfReflectBehindPixels = 4.0;
// How far past a cell's edge, in render pixels, a step lands, so the next step reads the cell beyond it.
static const float SdfReflectNudge = 0.005;
// The share of the screen's shorter side over which a hit fades toward the screen's edge.
static const float SdfReflectEdge = 0.08;
// The share of the step budget a hit may take at full confidence; past it a hit's share falls to none at the budget's end.
static const float SdfReflectBudgetFade = 0.75;

// Step zero's sums of the level-two texels the group's lanes hold and the history pixels each sum counts.
groupshared float4 sdfReflectColorSums[64];
groupshared float sdfReflectColorCounts[64];

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

    return (((sdfAirFogColor(ambient, direction, airLight) * weights.x) + (sdfAirHazeColor(ambient, direction, airLight) * weights.y)) + (sdfSky[0].MediumColor * weights.z));
}

// Where a mirror ray from `origin` along `direction` (relative to the eye of the view `rows` holds) meets the surface of the
// render pixel `hitPixel` its march reached, whose sample is `surface` (absolute) with geometric normal `normal`: the ray's
// crossing of that sample's tangent plane, absolute, where the crossing projects inside the pixel, and otherwise the sample.
// The march resolves which pixel the ray meets, at the pixel's edge or at its depth taken flat across it, so the sample
// alone would move what a ray reads a whole render pixel at a time along a reflected silhouette; the crossing moves it
// continuously, and lies on the surface wherever that surface is smooth across the pixel.
float3 sdfReflectCrossing(float4 rows[6], float3 origin, float3 direction, float3 surface, float3 normal, uint2 hitPixel) {
    float approach = dot(direction, normal);

    if (approach >= -1.0e-4) {
        return surface;
    }

    float travel = (dot(((surface - rows[0].xyz) - origin), normal) / approach);
    float3 crossing = (origin + (direction * travel));
    float2 projected;

    // The render grid's samples lie where the jittered rays met the scene, the pixel's at its centre.
    return (((travel > 0.0) && sdfProjectView(rows, crossing, projected) &&
        all(abs((projected - passGroup.jitter) - (float2(hitPixel) + 0.5)) <= 0.5))
        ? (crossing + rows[0].xyz) : surface);
}
// A mirror ray as the march reads it, relative to the view's eye: where it starts, its direction, its inverse forward
// depth at the start of its projected segment and that depth's change over the segment, which is linear in the share of
// the segment, and the scale that gives its travel at a share. A point's screen offset from the start is proportional to
// its travel over its forward depth, so travel is travelScale * share / (startInverse + rate * share).
struct SdfReflectRay {
    float3 origin;
    float3 direction;
    float startInverse;
    float rate;
    float travelScale;
};
// The ray's point at a share of its projected segment, relative to the view's eye.
float3 sdfReflectRayPoint(SdfReflectRay ray, float share) {
    return (ray.origin + (ray.direction * ((ray.travelScale * share) / (ray.startInverse + (ray.rate * share)))));
}
// Marches a ray's projected segment, from `start` along `delta` in render pixels, from where it leaves its own pixel,
// for at most `budget` reads of the pyramid, and returns the share of the segment at which it meets a surface, or -1 for
// none. `open` says whether the march passed in front of everything to the segment's end, `taken` how many reads it took,
// and `walked` how many render pixels it walked behind occluders just before the surface it met. A cell of a coarse level
// the ray stays in front of is skipped whole; a render pixel's own surface is its sample's tangent plane (its visibility
// record's distance along the pixel's ray and normal), which the ray meets where it crosses that plane from the front
// inside the pixel. A face turned from the ray is never met: the ray passes in front of it, or behind it.
float sdfReflectMarch(ViewportData view, uint viewIndex, float2 start, float2 delta, SdfReflectRay ray, uint budget, out bool open, out uint taken, out float walked) {
    uint2 dims = passGroup.imageExtent;
    float2 grid = float2(worldViewDims(view));
    SdfReflectDepthBases bases = sdfReflectDepthBases(dims);
    float2 inverse = float2(((delta.x != 0.0) ? (1.0 / delta.x) : 1.0e30), ((delta.y != 0.0) ? (1.0 / delta.y) : 1.0e30));
    uint2 toward = uint2((delta.x >= 0.0) ? 1u : 0u, (delta.y >= 0.0) ? 1u : 0u);
    float2 nudge = select((delta >= 0.0), SdfReflectNudge.xx, -SdfReflectNudge.xx);
    float span = max(abs(delta.x), abs(delta.y));
    float2 leaves = ((floor(start) + float2(toward) + nudge - start) * inverse);
    float share = max(min(leaves.x, leaves.y), 0.0);
    // Where the ray last went behind an occluder, as a share; negative while it is in front.
    float behindFrom = -1.0;
    uint level = 0u;

    open = false;
    taken = 0u;
    walked = 0.0;

    [loop]
    for (uint step = 0u; ((step < budget) && (share < 1.0)); step++) {
        uint2 pixel = (uint2)clamp((start + (delta * share)), float2(0.0, 0.0), (float2(dims) - 1.0));
        uint2 texel = (pixel >> level);
        float cellInverse = ((level == 0u)
            ? sdfReflectDepthPixel(pixel)
            : asfloat(sdfReflectionDepth[sdfReflectDepthWord(sdfReflectDepthBaseOf(bases, level), dims, level, texel)]));
        float rayInverse = (ray.startInverse + (ray.rate * share));
        float2 edges = ((float2((texel + toward) << level) + nudge - start) * inverse);
        float leave = min(edges.x, edges.y);

        sdfWorkSteps++;
        taken++;
        if ((level == 0u) && (cellInverse > 0.0)) {
            // A covered render pixel: the ray's side of its sample's tangent plane where it enters the pixel and where it
            // leaves it decides.
            uint record = worldVisibilityRecord(pixel, viewIndex);
            float3 normal = sdfVisibilityUnpackNormal(sdfVisibilityRecordBuffer[record + SdfVisibilityNormalWord]);
            float3 surface = (cameraRayDirection(view, ((float2(pixel) + 0.5) / grid)) * asfloat(sdfVisibilityRecordBuffer[record + SdfVisibilityTWord]));
            float approach = dot(ray.direction, normal);
            float entering = dot((sdfReflectRayPoint(ray, share) - surface), normal);

            if (entering >= 0.0) {
                behindFrom = -1.0;
                if ((approach < 0.0) && (dot((sdfReflectRayPoint(ray, min(leave, 1.0)) - surface), normal) <= 0.0)) {
                    return share;
                }
                share = leave;
                level = 1u;
                continue;
            }

            // Behind the plane where it enters: a face toward the ray within the occluder's thickness is met at the
            // pixel's edge, which the ray crossed it beyond; otherwise the ray passes behind it.
            float surfaceDepth = (1.0 / cellInverse);

            behindFrom = ((behindFrom < 0.0) ? share : behindFrom);
            walked = ((share - behindFrom) * span);
            if ((approach < 0.0) && (((1.0 / rayInverse) - surfaceDepth) <= (SdfReflectThickness + (SdfReflectThicknessShare * surfaceDepth)))) {
                return share;
            }
            if (walked >= SdfReflectBehindPixels) {
                return -1.0;
            }
            share = leave;
        } else if (rayInverse > cellInverse) {
            // In front of everything the cell holds: the ray leaves it, unless it falls behind the cell's nearest surface
            // first, where it enters the cell a level finer. An uncovered render pixel holds nothing.
            behindFrom = -1.0;
            float meets = (((ray.rate < 0.0) && (cellInverse > 0.0)) ? ((cellInverse - ray.startInverse) / ray.rate) : 2.0);

            if ((level > 0u) && (meets < leave) && (meets <= 1.0)) {
                share = max(share, meets);
                level--;
            } else {
                share = leave;
                level = min((level + 1u), SDF_REFLECTION_DEPTH_LEVELS);
            }
        } else if (level > 0u) {
            level--;
        } else {
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

    SdfReflectRay ray;

    ray.origin = origin;
    ray.direction = direction;
    ray.startInverse = startInverse;
    // A point's screen offset is proportional to its travel over its forward depth, so the clipped segment's share s
    // stands at travel u with u * (1/z(u)) = s * clip * reach / z(reach).
    ray.travelScale = ((clip * reach) * finishInverse);
    delta *= clip;
    finishInverse = lerp(startInverse, finishInverse, clip);
    ray.rate = (finishInverse - startInverse);

    if (max(abs(delta.x), abs(delta.y)) < 1.0) {
        return traced;
    }

    bool open;
    uint taken;
    float walked;
    float hitShare = sdfReflectMarch(p.view, p.viewIndex, start, delta, ray, passGroup.reflectionSteps, open, taken, walked);

    if (hitShare < 0.0) {
        // A ray that passed in front of everything it crossed and ended over open sky, away from the eye, sees the sky,
        // as far as its end stands from the screen's edge: a ray the edge cut short leaves the screen toward what it does
        // not show, so the volume answers it, and the share falls to that smoothly as a ray's end nears the edge.
        float2 end = (start + delta);
        float2 endEdge = (min(end, (dims - end)) / (SdfReflectEdge * min(dims.x, dims.y)));
        float sky = (glossy * smoothstep(0.0, 1.0, saturate(min(endEdge.x, endEdge.y))));

        return ((open && !towardEye && (sky > 0.0) && (sdfReflectDepthPixel((uint2)clamp(end, float2(0.0, 0.0), (dims - 1.0))) <= 0.0))
            ? sdfViewsReflectionPack(float3(0.0, 0.0, 0.0), 0.0, saturate(sky), normalWord, plane) : traced);
    }

    float2 at = (start + (delta * hitShare));
    uint2 hitPixel = (uint2)clamp(at, float2(0.0, 0.0), (dims - 1.0));
    uint hitRecord = worldVisibilityRecord(hitPixel, p.viewIndex);
    SdfVisibility hitVisibility = sdfLoadVisibility(hitRecord);
    float3 hitNormal = sdfVisibilityUnpackNormal(sdfVisibilityRecordBuffer[hitRecord + SdfVisibilityNormalWord]);
    float3 hitPoint = sdfReflectCrossing(rows, origin, direction, sdfPixelSurfacePoint(sdfPixelAt(p.view, hitPixel, p.viewIndex), hitVisibility.t),
        hitNormal, hitPixel);
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
    // footprint at the hit's distance from it. The prefiltered level whose texels span that radius answers it in one
    // filtered sample, the same every frame.
    float alpha = (material.roughness * material.roughness);
    float travelled = length((hitPoint - rows[0].xyz) - origin);
    float pixelMetres = (((2.0 * passGroup.previousView[1].w) / float(historyDims.y)) * previousT);
    float radius = ((2.0 * alpha * travelled) / max(pixelMetres, 1.0e-6));
    float4 sum = sdfReflectColorFootprint(uv, historyDims, radius);

    // The air between the preceding camera and the hit, which the composite added after the history was written.
    uint2 centre = min((uint2)(uv * float2(historyDims)), (historyDims - 1u));

    sum.rgb += (sdfReflectInScatter(((centre.y * historyDims.x) + centre.x), historyDims, (previousRay / max(previousT, 1.0e-6)))
        * sdfStoredLightScale(passGroup.debugMode, passGroup.lightScale));

    // The history is premultiplied by coverage: what of the footprint no surface covered is the open sky beyond it.
    float2 edge = (min(at, (dims - at)) / (SdfReflectEdge * min(dims.x, dims.y)));
    float budget = float(max(passGroup.reflectionSteps, 1u));
    float late = smoothstep((SdfReflectBudgetFade * budget), budget, float(taken));
    // The farther a ray walked behind an occluder before what it met, the likelier the occluder is thicker than taken and
    // hides that surface: its share falls to none where the march gives such a ray up.
    float hidden = smoothstep(0.0, SdfReflectBehindPixels, walked);
    float confidence = (glossy * smoothstep(0.0, 1.0, saturate(min(edge.x, edge.y))) * (1.0 - late) * (1.0 - hidden));
    float coverage = saturate(sum.a);

    if (!all(isfinite(sum)) || (confidence <= 0.0)) {
        return traced;
    }
    return sdfViewsReflectionPack((sum.rgb / max(sum.a, 1.0e-4)), (confidence * coverage), (confidence * (1.0 - coverage)), normalWord, plane);
}

// Step zero: each lane takes one texel of level two of the reflection colour, the 4 by 4 history pixels under it, writes
// its four texels of level one and its own, and the group reduces its 8 by 8 texels of level two to the levels above in
// its own memory, each texel the mean of the history pixels inside the extent under it.
void sdfReflectPrefilter(uint2 group, uint2 lane) {
    uint2 dims = passGroup.viewsRate.zw;
    SdfReflectDepthBases bases = sdfReflectDepthBases(dims);
    uint2 texel = ((group * 8u) + lane);
    float4 sum = float4(0.0, 0.0, 0.0, 0.0);
    float count = 0.0;

    [unroll]
    for (uint child = 0u; (child < 4u); child++) {
        uint2 fine = ((texel * 2u) + uint2((child & 1u), (child >> 1u)));
        float4 fineSum = float4(0.0, 0.0, 0.0, 0.0);
        float fineCount = 0.0;

        [unroll]
        for (uint corner = 0u; (corner < 4u); corner++) {
            uint2 pixel = ((fine * 2u) + uint2((corner & 1u), (corner >> 1u)));

            if (all(pixel < dims)) {
                fineSum += sdfReflectionHistory.Load(int3(int2(pixel), 0));
                fineCount += 1.0;
            }
        }
        if (fineCount > 0.0) {
            uint word = sdfReflectColorWord(sdfReflectDepthBaseOf(bases, 1u), dims, 1u, fine);
            uint2 packed = sdfReflectColorPack(fineSum / fineCount);

            sdfReflectionColorRW[word] = packed.x;
            sdfReflectionColorRW[word + 1u] = packed.y;
        }
        sum += fineSum;
        count += fineCount;
    }
    if (count > 0.0) {
        uint word = sdfReflectColorWord(sdfReflectDepthBaseOf(bases, 2u), dims, 2u, texel);
        uint2 packed = sdfReflectColorPack(sum / count);

        sdfReflectionColorRW[word] = packed.x;
        sdfReflectionColorRW[word + 1u] = packed.y;
    }
    sdfReflectColorSums[(lane.y * 8u) + lane.x] = sum;
    sdfReflectColorCounts[(lane.y * 8u) + lane.x] = count;

    // Levels three and up: at each, the lanes on the level's grid take the sums of their block's four texels of the level
    // below, which the lanes beside them hold.
    [unroll]
    for (uint level = 3u; (level <= SDF_REFLECTION_COLOR_LEVELS); level++) {
        uint span = (1u << (level - 3u));
        bool leads = all((lane & ((span << 1u) - 1u)) == 0u);
        uint at = ((lane.y * 8u) + lane.x);

        GroupMemoryBarrierWithGroupSync();
        if (leads) {
            sum = (((sum + sdfReflectColorSums[at + span]) + sdfReflectColorSums[at + (span * 8u)]) + sdfReflectColorSums[at + (span * 9u)]);
            count = (((count + sdfReflectColorCounts[at + span]) + sdfReflectColorCounts[at + (span * 8u)]) + sdfReflectColorCounts[at + (span * 9u)]);
        }
        GroupMemoryBarrierWithGroupSync();
        if (leads) {
            sdfReflectColorSums[at] = sum;
            sdfReflectColorCounts[at] = count;
            if (count > 0.0) {
                uint word = sdfReflectColorWord(sdfReflectDepthBaseOf(bases, level), dims, level, (texel >> (level - 2u)));
                uint2 packed = sdfReflectColorPack(sum / count);

                sdfReflectionColorRW[word] = packed.x;
                sdfReflectionColorRW[word + 1u] = packed.y;
            }
        }
    }
}

#if SDF_VISIBILITY_GROUP_EDGE != 8
#error Step zero's group is 8 by 8 texels of level two.
#endif

[numthreads(SDF_VISIBILITY_GROUP_EDGE, SDF_VISIBILITY_GROUP_EDGE, 1)]
void CSMain(uint3 id : SV_DispatchThreadID, uint3 lane : SV_GroupThreadID, uint3 group : SV_GroupID) {
    // The pushed index is uniform over the dispatch, so every lane of a group reaches step zero's barriers.
    if (pushedIndex.index == 0u) {
        // A history of another epoch holds light at another scale; step one reads nothing then (sdfReflectTrace).
        if ((passGroup.historyFrames != 0u) && all(passGroup.viewsRate.zw != 0u)) {
            sdfReflectPrefilter(group.xy, lane.xy);
        }
        return;
    }

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
    puckCountShading(0u, sdfWorkEnvironmentLookups);
}
