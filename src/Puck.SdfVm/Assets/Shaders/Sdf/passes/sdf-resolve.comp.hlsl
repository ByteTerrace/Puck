// The view's one path from its render extent to its output extent (SdfWorldPackage.Resolve), one invocation an output
// pixel. It resolves the lit image, color premultiplied by coverage and fog transmittance with coverage in alpha, which
// only views wrote, and only inside the dispatch box cull-args left: every render sample outside it reads as transparent.
// Beside it, it resolves each sample's surface transport (shade/sdf-transport.hlsli), computed from the sample's coverage
// and its own visibility record's ray distance along the output pixel's ray, with exactly the color's weights, so the
// composite's atmosphere and media see the coverage the color has. Spatial, it reconstructs the active render grid without sampling unrendered ceiling pixels
// (reconstruction.hlsli), reading both quantities' taps over one footprint. Temporal (passGroup.temporal, with no debug
// view), the history holds, per output pixel, the weighted mean of every jittered sample the pixel has gathered,
// coverage with color and transport, and their summed weight:
// - this frame's samples are the 3x3 render samples nearest the output pixel's center, each weighted by a Gaussian of
//   its distance in output pixels;
// - the pixel's motion is the nearest-depth sample's, reprojected through sdfReprojection's modules, or, where the 3x3
//   saw no surface, the camera's rotation alone;
// - the history at the moved position is rejected when the history surface there names another identity, or a ray
//   distance more than SdfHistoryDepthTolerance from the reprojected one; the pixel then shows the spatial path at this
//   frame's sample grid and its history restarts from this frame's samples;
// - surviving history is clipped to the 3x3's YCoCg, coverage and transport box, its weight lowered by the reactivity,
//   and joined by this frame's samples; until a full sample's weight is gathered the spatial path shows through.
// The first frame of an epoch reads no history and writes the spatial path's color and transport word, computed at the
// one site below that both paths run, so it is the spatial path's frame exactly. Where the output has the render grid's
// extent and no jitter, each pixel is its render sample copied whole, and its transport word carries that sample's ray
// distance, which the composite turns into transport with a native view's own arithmetic. The history surface holds the
// gathered weight, capped at one jitter period of full-weight samples. Beside the lit image it writes each output
// pixel's transport words (sdf-transport.hlsli), which the composite scales each atmosphere kind's in-scatter by and clips
// the volumes by. The sky never enters the history: the sky and composite passes follow this one.
#define SDF_DYNAMIC_TRANSFORMS
#include "../isa/sdf-resolve.interface.hlsli"
#include "../frame/sdf-viewport.hlsli"
#include "../frame/sdf-reprojection.hlsli"
#include "../frame/sdf-work.hlsli"
#include "../shade/sdf-transport.hlsli"
#include "../../../../../Puck.Shaders/Assets/Shaders/Shared/reconstruction.hlsli"

// The words an output pixel holds in the history surface: the nearest surface's identity, then its ray distance and the
// gathered weight as two half floats (the distance low; the far limit 8192 and the weight cap stay normal halves, each
// within a relative 2^-11, far inside SdfHistoryDepthTolerance), then the accumulated transport's two words
// (sdfPackTransport). KEEP IN SYNC with SdfWorldPackage.HistorySurfaceWords.
static const uint SdfHistorySurfaceWords = 4u;
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
// The history at a continuous output position, the first texel's center at zero: Catmull-Rom over the sixteen nearest
// texels, each clamped to the image, the color from the history color and the transport from the history surface with
// one set of weights.
void sdfHistoryAt(float2 position, uint2 dims, out float4 color, out float4 transport) {
    float2 origin = floor(position);
    float2 f = (position - origin);
    float4 wx = puckCatmullRomWeights(f.x);
    float4 wy = puckCatmullRomWeights(f.y);
    int2 corner = (int2(origin) - 1);

    color = float4(0.0, 0.0, 0.0, 0.0);
    transport = float4(0.0, 0.0, 0.0, 0.0);
    [unroll] for (int y = 0; y < 4; y++) {
        float4 rowColor = float4(0.0, 0.0, 0.0, 0.0);
        float4 rowTransport = float4(0.0, 0.0, 0.0, 0.0);

        [unroll] for (int x = 0; x < 4; x++) {
            uint2 texel = sdfResolveClamp((corner + int2(x, y)), dims);

            rowColor += (wx[x] * historyColor.Load(int3(texel, 0)));
            uint word = ((SdfHistorySurfaceWords * ((texel.y * dims.x) + texel.x)) + 2u);

            rowTransport += (wx[x] * sdfUnpackTransport(uint2(historySurface[word], historySurface[word + 1u])));
        }
        color += (wy[y] * rowColor);
        transport += (wy[y] * rowTransport);
    }
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
// The output pixel's unjittered ray, which every render sample of its footprint carries its transport along, as the
// composite's sdfSkyPassDirection takes it.
float3 sdfResolveAirDirection(uint2 pixel, uint2 extent) {
    ViewportData view = worldView();

    view.lens.yz = passGroup.frustumOffset;

    return cameraRayDirection(view, ((float2(pixel) + 0.5) / float2(extent)));
}
// A render sample's transport beside the color tap read at the same pixel: zero where that tap has no coverage, which
// every tap outside the dispatch box has, and for a sample with no surface.
float4 sdfResolveTransportAt(int2 pixel, uint2 render, float coverage, float3 origin, float3 direction) {
    if (coverage <= 0.0) {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    SdfVisibility visibility = sdfLoadVisibility(sdfVisibilityRecord(sdfResolveClamp(pixel, render), 0u, render));

    return (sdfVisibilityHit(visibility) ? sdfSampleTransport(coverage, visibility.t, origin, direction) : float4(0.0, 0.0, 0.0, 0.0));
}
// The spatial path: the lit color and its transport at a continuous render-grid position, each reconstructed from the
// same taps with one set of weights, and the transport word the composite reads; or, where the output has the grid's
// extent and no jitter, the render pixel itself, whose word is its sample's ray distance (sdfTransportSampleWord), read
// from the record exactly where a native view's composite reads it.
void sdfResolveSpatial(uint2 pixel, float2 position, bool exact, uint2 render, uint4 current, float3 origin, float3 direction, out float4 color, out float4 transport, out uint2 word) {
    if (exact) {
        float t = 0.0;

        color = puckReconstructionTapWithin(currentColor, int2(pixel), render, uint2(0, 0), current);
        if (SDF_VISIBILITY_CURRENT(pixel, cullBounds)) {
            SdfVisibility visibility = sdfLoadVisibility(sdfVisibilityRecord(pixel, 0u, render));

            t = (sdfVisibilityHit(visibility) ? visibility.t : 0.0);
        }
        transport = sdfSampleTransport(color.a, t, origin, direction);
        word = sdfTransportSampleWord(t);
        return;
    }

    PuckReconstructionFootprint footprint = puckReconstructionFootprintAt(position, render, passGroup.upscaleSharpness);
    float4 colors[16];
    float4 transports[16];

    [unroll] for (uint tap = 0u; tap < 16u; tap++) {
        colors[tap] = float4(0.0, 0.0, 0.0, 0.0);
        transports[tap] = float4(0.0, 0.0, 0.0, 0.0);
        if (puckReconstructionReads(footprint, tap)) {
            int2 at = (footprint.origin + puckReconstructionTapOffset(tap));

            colors[tap] = puckReconstructionTapWithin(currentColor, at, render, uint2(0, 0), current);
            transports[tap] = sdfResolveTransportAt(at, render, colors[tap].a, origin, direction);
        }
    }
    color = puckReconstructionCombine(footprint, colors);
    transport = puckReconstructionCombine(footprint, transports);
    word = sdfPackTransport(transport);
}

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint2 extent = passGroup.extent;
    if (any(id.xy >= extent)) return;
    uint2 render = passGroup.imageExtent;
    float2 jitter = passGroup.jitter;
    // Only the dispatch box holds this frame's lit samples: the box cull-args wrote, in render pixels.
    uint4 current = (uint4(cullBounds[0], cullBounds[1], cullBounds[2], cullBounds[3]) * SDF_VISIBILITY_BOX_EDGE);
    bool temporal = ((passGroup.temporal != 0u) && (passGroup.debugMode == 0u));
    // The spatial path at this frame's sample grid: the sample of render pixel i lies at i + 0.5 + jitter.
    bool unjittered = (!temporal || all(jitter == 0.0));
    float3 airOrigin = passGroup.viewPosition;
    float3 airDirection = sdfResolveAirDirection(id.xy, extent);
    float4 spatial;
    float4 spatialTransport;
    uint2 spatialWord;

    sdfResolveSpatial(id.xy, (((((float2(id.xy) + 0.5) * float2(render)) / float2(extent)) - 0.5) - (unjittered ? float2(0.0, 0.0) : jitter)),
        (unjittered && all(render == extent)), render, current, airOrigin, airDirection, spatial, spatialTransport, spatialWord);
    if (!temporal) {
        output[id.xy] = spatial;
        sdfWorkTexels = 1u;
        transportRW[((id.y * extent.x) + id.x)] = spatialWord;
        puckCountWork(sdfWorkSteps, sdfWorkTexels);
        return;
    }

    float2 scale = (float2(extent) / float2(render));
    float2 center = ((float2(id.xy) + 0.5) / scale);
    int2 nearest = int2(floor(center - jitter));
    float4 sum = float4(0.0, 0.0, 0.0, 0.0);
    float4 transportSum = float4(0.0, 0.0, 0.0, 0.0);
    float weightSum = 0.0;
    float reactive = 0.0;
    float3 boxMin = float3(3.402823e+38, 3.402823e+38, 3.402823e+38);
    float3 boxMax = -boxMin;
    float coverageMin = 1.0;
    float coverageMax = 0.0;
    float4 transportMin = float4(3.402823e+38, 3.402823e+38, 3.402823e+38, 3.402823e+38);
    float4 transportMax = float4(0.0, 0.0, 0.0, 0.0);
    bool hit = false;
    float hitT = 0.0;
    uint hitIdentity = 0u;
    uint hitRecord = 0u;
    uint2 hitPixel = uint2(0u, 0u);

    [unroll] for (int dy = -1; dy <= 1; dy++) {
        [unroll] for (int dx = -1; dx <= 1; dx++) {
            uint2 pixel = sdfResolveClamp((nearest + int2(dx, dy)), render);
            bool current = SDF_VISIBILITY_CURRENT(pixel, cullBounds);
            float4 color = (current ? currentColor.Load(int3(pixel, 0)) : float4(0.0, 0.0, 0.0, 0.0));
            float2 offset = (((float2(pixel) + 0.5 + jitter) - center) * scale);
            float weight = exp(-SdfResolveFilterFalloff * dot(offset, offset));
            float3 ycocg = sdfToYCoCg(color.rgb);
            float4 transport = float4(0.0, 0.0, 0.0, 0.0);

            if (current) {
                reactive = max(reactive, reactivity[sdfReactivityIndex(pixel, 0u, render)]);

                uint record = sdfVisibilityRecord(pixel, 0u, render);
                SdfVisibility visibility = sdfLoadVisibility(record);

                bool surface = sdfVisibilityHit(visibility);

                if (surface) {
                    transport = sdfSampleTransport(color.a, visibility.t, airOrigin, airDirection);
                }
                if (surface && (!hit || (visibility.t < hitT))) {
                    hit = true;
                    hitT = visibility.t;
                    hitIdentity = visibility.identity;
                    hitRecord = record;
                    hitPixel = pixel;
                }
            }
            sum += (weight * color);
            transportSum += (weight * transport);
            weightSum += weight;
            boxMin = min(boxMin, ycocg);
            boxMax = max(boxMax, ycocg);
            coverageMin = min(coverageMin, color.a);
            coverageMax = max(coverageMax, color.a);
            transportMin = min(transportMin, transport);
            transportMax = max(transportMax, transport);
        }
    }

    reactive = saturate(reactive);
    bool accepted = false;
    float2 historyPosition = float2(0.0, 0.0);
    float historyWeightGathered = 0.0;

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
            // An uncovered pixel holds nothing the camera's translation moves, so it moves with the camera's rotation alone.
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
                uint historyIdentity = historySurface[word];
                uint distanceAndWeight = historySurface[word + 1u];
                float historyT = f16tof32(distanceAndWeight & 0xFFFFu);

                historyWeightGathered = f16tof32(distanceAndWeight >> 16u);
                accepted = ((historyIdentity == hitIdentity) &&
                    (!hit || (abs(historyT - previousT) <= (SdfHistoryDepthTolerance * previousT))));
            }
        }
    }

    // The weighted mean of every sample the pixel has gathered: history clipped to this frame's neighbourhood and
    // weighed down by reactivity, then this frame's samples, the transport with the color's weights. While less than one
    // sample's full weight is gathered, the spatial path shows through.
    float historyWeight = 0.0;
    float4 history = float4(0.0, 0.0, 0.0, 0.0);
    float4 historyTransport = float4(0.0, 0.0, 0.0, 0.0);

    if (accepted) {
        float4 previous;
        float4 previousTransport;

        sdfHistoryAt(historyPosition, extent, previous, previousTransport);
        // Clipping infinity can produce NaN, and even zero history weight cannot remove it (NaN * 0 is NaN).
        accepted = (all(isfinite(previous)) && all(isfinite(previousTransport)));
        if (accepted) {
            historyWeight = (clamp(historyWeightGathered, 0.0, SdfHistoryWeightCap) * (1.0 - reactive));
            history = float4(
                sdfFromYCoCg(sdfClipToBox(sdfToYCoCg(max(previous.rgb, 0.0)), boxMin, boxMax)),
                clamp(previous.a, coverageMin, coverageMax)
            );
            historyTransport = clamp(previousTransport, transportMin, transportMax);
        }
    }

    float total = (historyWeight + weightSum);
    float4 accumulated = (((history * historyWeight) + sum) / max(total, 1.0e-6));
    float4 accumulatedTransport = (((historyTransport * historyWeight) + transportSum) / max(total, 1.0e-6));
    float4 resolved = (accepted ? lerp(spatial, accumulated, saturate(total)) : spatial);

    uint word = (SdfHistorySurfaceWords * ((id.y * extent.x) + id.x));

    output[id.xy] = resolved;
    sdfWorkTexels = 1u;
    // The working history is half-float. A non-finite current sample contributes no reusable history, and a finite
    // reconstruction must remain representable when stored, so one bright transient cannot poison later frames.
    bool reusable = (all(isfinite(accumulated)) && all(isfinite(accumulatedTransport)) && isfinite(total));

    transportRW[((id.y * extent.x) + id.x)] = (accepted ? sdfPackTransport(lerp(spatialTransport, accumulatedTransport, saturate(total))) : spatialWord);
    historyColorRW[id.xy] = (reusable ? clamp(accumulated, -65504.0, 65504.0) : float4(0.0, 0.0, 0.0, 0.0));
    historySurfaceRW[word] = hitIdentity;
    historySurfaceRW[word + 1u] = (f32tof16(hit ? hitT : 0.0) | (f32tof16(reusable ? min(total, SdfHistoryWeightCap) : 0.0) << 16u));
    uint2 historyTransportWords = sdfPackTransport(reusable ? accumulatedTransport : float4(0.0, 0.0, 0.0, 0.0));

    historySurfaceRW[word + 2u] = historyTransportWords.x;
    historySurfaceRW[word + 3u] = historyTransportWords.y;
    puckCountWork(sdfWorkSteps, sdfWorkTexels);
}
