// The view's one path from its render extent to its output extent (SdfWorldPackage.Resolve), one invocation an output
// pixel. Spatial, it reconstructs the active render grid without sampling unrendered ceiling pixels
// (reconstruction.hlsli). Temporal (passGroup.temporal, with no debug view), the history holds, per output pixel, the
// weighted mean of every jittered sample the pixel has gathered and their summed weight:
// - this frame's samples are the 3x3 render samples nearest the output pixel's center, each weighted by a Gaussian of
//   its distance in output pixels;
// - the pixel's motion is the nearest-depth sample's, reprojected through sdfReprojection's modules, or, where the 3x3
//   saw no surface, the camera's rotation alone;
// - the history at the moved position is rejected when the history surface there names another identity, or a ray
//   distance more than SdfHistoryDepthTolerance from the reprojected one; the pixel then shows the spatial path at this
//   frame's sample grid and its history restarts from this frame's samples;
// - surviving history is clipped to the 3x3's YCoCg box, its weight lowered by the reactivity, and joined by this
//   frame's samples; until a full sample's weight is gathered the spatial path shows through.
// The first frame of an epoch reads no history and is the spatial path's frame exactly. The history color's alpha holds
// the gathered weight, capped at one jitter period of full-weight samples; the output's alpha is the coverage the render
// grid carries.
#define SDF_DYNAMIC_TRANSFORMS
#include "../isa/sdf-resolve.interface.hlsli"
#include "../../../../../Puck.Shaders/Assets/Shaders/Shared/reconstruction.hlsli"
#include "../frame/sdf-viewport.hlsli"
#include "../frame/sdf-reprojection.hlsli"
#include "../frame/sdf-work.hlsli"

// KEEP IN SYNC with SdfWorldPackage.HistorySurfaceWords.
static const uint SdfHistorySurfaceWords = 2u;
// The relative ray-distance disagreement a reprojected history sample may carry and still describe the same surface.
static const float SdfHistoryDepthTolerance = 0.05;
// The accumulated weight history saturates at: one jitter period of full-weight samples. KEEP IN SYNC with
// SdfTemporalHistory.Period.
static const float SdfHistoryWeightCap = 8.0;
// The falloff of the Gaussian a sample is weighted by, over its squared distance in output pixels from the output
// pixel's center: 1 / (2 * 0.35^2), a reconstruction about as wide as one output pixel's box.
static const float SdfResolveFilterFalloff = 4.08;

uint2 sdfResolveClamp(int2 pixel, uint2 dims) {
    return (uint2)clamp(pixel, int2(0, 0), (int2(dims) - 1));
}
float3 sdfToYCoCg(float3 color) {
    return float3(
        ((0.25 * color.r) + (0.5 * color.g) + (0.25 * color.b)),
        ((0.5 * color.r) - (0.5 * color.b)),
        ((-0.25 * color.r) + (0.5 * color.g) - (0.25 * color.b))
    );
}
float3 sdfFromYCoCg(float3 color) {
    float t = (color.x - color.z);

    return float3((t + color.y), (color.x + color.z), (t - color.y));
}
// Moves a color outside an axis-aligned box toward the box's center until it lies on the box.
float3 sdfClipToBox(float3 color, float3 boxMin, float3 boxMax) {
    float3 center = (0.5 * (boxMin + boxMax));
    float3 extent = max((0.5 * (boxMax - boxMin)), 1.0e-6);
    float3 offset = (color - center);
    float3 units = abs(offset / extent);
    float reach = max(units.x, max(units.y, units.z));

    return ((reach > 1.0) ? (center + (offset / reach)) : color);
}
// The history color at a continuous output position, the first texel's center at zero: Catmull-Rom over the sixteen
// nearest texels, each clamped to the image.
float4 sdfHistoryAt(float2 position, uint2 dims) {
    float2 origin = floor(position);
    float2 f = (position - origin);
    float4 wx = puckCatmullRomWeights(f.x);
    float4 wy = puckCatmullRomWeights(f.y);
    int2 corner = (int2(origin) - 1);
    float4 sum = float4(0.0, 0.0, 0.0, 0.0);

    [unroll] for (int y = 0; y < 4; y++) {
        float4 row = float4(0.0, 0.0, 0.0, 0.0);

        [unroll] for (int x = 0; x < 4; x++) {
            row += (wx[x] * historyColor.Load(int3(sdfResolveClamp((corner + int2(x, y)), dims), 0)));
        }
        sum += (wy[y] * row);
    }
    return sum;
}
// The view's own camera rows, unjittered, as sdfProjectView reads a view.
void sdfCurrentViewRows(out float4 rows[6]) {
    rows[0] = float4(passGroup.viewPosition, 1.0);
    rows[1] = float4(passGroup.viewRight, passGroup.tanHalfFieldOfView);
    rows[2] = float4(passGroup.viewUp, passGroup.aspectRatio);
    rows[3] = float4(passGroup.viewForward, 0.0);
    rows[4] = float4((float2)passGroup.imageExtent, 0.0, 0.0);
    rows[5] = float4(passGroup.nearDistance, passGroup.frustumOffset, 0.0);
}

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint2 extent = passGroup.extent;
    if (any(id.xy >= extent)) return;
    uint2 render = passGroup.imageExtent;
    float2 jitter = passGroup.jitter;
    bool temporal = ((passGroup.temporal != 0u) && (passGroup.debugMode == 0u));
    // The spatial path at this frame's sample grid: the sample of render pixel i lies at i + 0.5 + jitter.
    float4 spatial = ((!temporal || all(jitter == 0.0))
        ? puckReconstruct(currentColor, id.xy, extent, render, passGroup.upscaleSharpness)
        : puckReconstructAt(currentColor, ((((float2(id.xy) + 0.5) * float2(render)) / float2(extent)) - 0.5 - jitter), render, uint2(0, 0), passGroup.upscaleSharpness));

    if (!temporal) {
        output[id.xy] = spatial;
        sdfWorkTexels = 1u;
        puckCountWork(sdfWorkSteps, sdfWorkTexels);
        return;
    }

    float2 scale = (float2(extent) / float2(render));
    float2 center = ((float2(id.xy) + 0.5) / scale);
    int2 nearest = int2(floor(center - jitter));
    float3 sum = float3(0.0, 0.0, 0.0);
    float weightSum = 0.0;
    float reactive = 0.0;
    float3 boxMin = float3(3.402823e+38, 3.402823e+38, 3.402823e+38);
    float3 boxMax = -boxMin;
    bool hit = false;
    float hitT = 0.0;
    uint hitIdentity = 0u;
    uint hitRecord = 0u;
    uint2 hitPixel = uint2(0u, 0u);

    [unroll] for (int dy = -1; dy <= 1; dy++) {
        [unroll] for (int dx = -1; dx <= 1; dx++) {
            uint2 pixel = sdfResolveClamp((nearest + int2(dx, dy)), render);
            float3 color = currentColor.Load(int3(pixel, 0)).rgb;
            float2 offset = (((float2(pixel) + 0.5 + jitter) - center) * scale);
            float weight = exp(-SdfResolveFilterFalloff * dot(offset, offset));
            float3 ycocg = sdfToYCoCg(color);

            sum += (weight * color);
            weightSum += weight;
            boxMin = min(boxMin, ycocg);
            boxMax = max(boxMax, ycocg);
            reactive = max(reactive, reactivity[sdfReactivityIndex(pixel, 0u, render)]);

            if (SDF_VISIBILITY_CURRENT(pixel, cullBounds)) {
                uint record = sdfVisibilityRecord(pixel, 0u, render);
                SdfVisibility visibility = sdfLoadVisibility(record);

                if (sdfVisibilityHit(visibility) && (!hit || (visibility.t < hitT))) {
                    hit = true;
                    hitT = visibility.t;
                    hitIdentity = visibility.identity;
                    hitRecord = record;
                    hitPixel = pixel;
                }
            }
        }
    }

    reactive = saturate(reactive);
    bool accepted = false;
    float2 historyPosition = float2(0.0, 0.0);

    if ((passGroup.historyFrames != 0u) && (passGroup.previousView[0].w != 0.0)) {
        float4 currentRows[6];
        sdfCurrentViewRows(currentRows);
        ViewportData view = worldView();
        bool moved = false;
        float2 previousPixel = float2(0.0, 0.0);
        float2 currentPixel = float2(0.0, 0.0);
        float previousT = 0.0;

        if (hit) {
            // The point the nearest-depth sample's own jittered ray reached, carried by its shape or triangle.
            float3 direction = cameraRayDirection(view, ((float2(hitPixel) + 0.5) / float2(render)));
            float3 surfacePoint = (view.position.xyz + (direction * hitT));
            float3 previousPoint;

            if (sdfPreviousPoint(hitRecord, sdfLoadVisibility(hitRecord), surfacePoint, previousPoint)) {
                float3 relative = (previousPoint - passGroup.previousView[0].xyz);

                previousT = length(relative);
                moved = (sdfProjectView(passGroup.previousView, relative, previousPixel) &&
                    sdfProjectView(currentRows, (surfacePoint - view.position.xyz), currentPixel));
            }
        } else {
            // The sky is a function of direction alone, so it moves with the camera's rotation alone.
            view.lens.yz = passGroup.frustumOffset;
            float3 direction = (cameraRayDirection(view, ((float2(id.xy) + 0.5) / float2(extent))) * passGroup.farDistance);

            moved = (sdfProjectView(passGroup.previousView, direction, previousPixel) &&
                sdfProjectView(currentRows, direction, currentPixel));
        }
        if (moved) {
            float2 motion = ((previousPixel / passGroup.previousView[4].xy) - (currentPixel / float2(render)));

            historyPosition = (((float2(id.xy) + 0.5) + (motion * float2(extent))) - 0.5);
            if (all(historyPosition >= -0.5) && all(historyPosition <= (float2(extent) - 0.5))) {
                uint2 texel = sdfResolveClamp(int2(floor(historyPosition + 0.5)), extent);
                uint word = (SdfHistorySurfaceWords * ((texel.y * extent.x) + texel.x));
                float historyT = asfloat(historySurface[word]);
                uint historyIdentity = historySurface[word + 1u];

                accepted = ((historyIdentity == hitIdentity) &&
                    (!hit || (abs(historyT - previousT) <= (SdfHistoryDepthTolerance * previousT))));
            }
        }
    }

    // The weighted mean of every sample the pixel has gathered: history clipped to this frame's neighbourhood and
    // weighed down by reactivity, then this frame's samples. While less than one sample's full weight is gathered, the
    // spatial path shows through.
    float historyWeight = 0.0;
    float3 history = float3(0.0, 0.0, 0.0);

    if (accepted) {
        float4 previous = sdfHistoryAt(historyPosition, extent);

        historyWeight = (clamp(previous.a, 0.0, SdfHistoryWeightCap) * (1.0 - reactive));
        history = sdfFromYCoCg(sdfClipToBox(sdfToYCoCg(max(previous.rgb, 0.0)), boxMin, boxMax));
    }

    float total = (historyWeight + weightSum);
    float3 accumulated = (((history * historyWeight) + sum) / max(total, 1.0e-6));
    float3 resolved = (accepted ? lerp(spatial.rgb, accumulated, saturate(total)) : spatial.rgb);

    uint word = (SdfHistorySurfaceWords * ((id.y * extent.x) + id.x));

    output[id.xy] = float4(resolved, spatial.a);
    sdfWorkTexels = 1u;
    historyColorRW[id.xy] = float4(accumulated, min(total, SdfHistoryWeightCap));
    historySurfaceRW[word] = asuint(hit ? hitT : 0.0);
    historySurfaceRW[word + 1u] = hitIdentity;
    puckCountWork(sdfWorkSteps, sdfWorkTexels);
}
