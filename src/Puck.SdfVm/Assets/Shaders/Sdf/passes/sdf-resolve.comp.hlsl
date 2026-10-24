// The view's one path from its render extent to its output extent (SdfWorldPackage.Resolve), one invocation an output
// pixel. It resolves the lit image, color premultiplied by coverage and fog transmittance with coverage in alpha, which
// views wrote for every render sample.
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
// - the history at the moved position is rejected when the receiver the history surface there names (the tap nearest
//   that pixel's center that is not a sub-pixel feature) matches no 3x3 tap's identity within SdfHistoryDepthTolerance
//   of its ray distance; the pixel then shows the spatial path at this frame's sample grid and its history restarts from
//   this frame's samples;
// - surviving history is clipped to the 3x3's YCoCg, coverage and transport box, its weight lowered by the reactivity,
//   and joined by this frame's samples; until a full sample's weight is gathered the spatial path shows through, and a
//   reactive pixel shows it by its reactivity;
// - a sub-pixel feature (sdfResolveFeature) the footprint showed within the last jitter period widens the box to its
//   color while the surface stands still, so the mean the jitter gathers over the feature survives the frames that miss
//   it, as a supersampled image holds it, and the history saturates at SDF_HISTORY_FEATURE_WEIGHT_CAP rather than one
//   period, so the mean barely pulses with the jitter.
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
#include "../frame/sdf-debug-modes.hlsli"
#include "../frame/sdf-reprojection.hlsli"
#include "../frame/sdf-work.hlsli"
#include "../shade/sdf-transport.hlsli"
#include "../../../../../Puck.Shaders/Assets/Shaders/Shared/reconstruction.hlsli"

// The history surface holds SDF_HISTORY_SURFACE_WORDS an output pixel (SdfWorldPackage.HistorySurfaceWords): the identity
// of the receiver tap nearest the pixel's center, then its ray distance and the gathered weight as two half floats (the
// distance low; the far limit 8192 and the weight cap stay normal halves, each within a relative 2^-11, far inside
// SdfHistoryDepthTolerance), then the accumulated transport's two words (sdfPackTransport), then the remembered feature:
// its YCoCg as three half floats, Y low and Co high in the fifth word, Cg low in the sixth with the frames the memory has
// left above it, zero for none. The weight saturates at SDF_HISTORY_WEIGHT_CAP, one jitter period of full-weight samples
// (SdfTemporalSamples.Period), or at SDF_HISTORY_FEATURE_WEIGHT_CAP while the pixel carries a feature.
// The falloff of the Gaussian a sample is weighted by, over its squared distance in output pixels from the output
// pixel's center: 1 / (2 * 0.35^2), a reconstruction about as wide as one output pixel's box.
static const float SdfResolveFilterFalloff = 4.08;
// A render sample is a sub-pixel feature when, in at least three of the four directions through it (across, down and
// the two diagonals), both of its neighbours lie this share of the brighter luma below it, or it below them: a dot in all
// four, a line thinner than a sample in the three its own direction is not. The corner of an edge keeps a neighbour on its
// own side in at least two directions, so an edge sweeping across a surface, a shadow's, never counts.
static const float SdfResolveFeatureContrast = 0.5;
// The most, in output pixels, a pixel's history may have moved and still carry its remembered feature into the box: a
// feature moves with its surface through the reprojection, but one that moves over a surface, a glint under a turning
// camera, must be clipped like any change.
static const float SdfResolveFeatureTravel = 0.05;

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
// Projects each component to its measured interval. A thin chroma interval must not amplify a half-float rounding
// difference into a luminance change: moving toward the box's center couples those otherwise independent corrections.
float3 sdfClipToBox(float3 color, float3 boxMin, float3 boxMax) {
    return clamp(color, boxMin, boxMax);
}
// Whether the luma at the center of a 5x5 window is a sub-pixel feature (SdfResolveFeatureContrast) against its eight
// neighbours, and its contrast against their mean. `window` is row-major, `at` the index of the sample tested, one of
// the inner nine.
bool sdfResolveFeature(float window[25], uint at, out float contrast) {
    float center = window[at];
    uint above = 0u;
    uint below = 0u;
    float neighbours = 0.0;
    // The four directions through the sample as window offsets: across, down, and the two diagonals.
    const uint steps[4] = { 1u, 5u, 6u, 4u };

    [unroll] for (uint direction = 0u; direction < 4u; direction++) {
        float a = window[at + steps[direction]];
        float b = window[at - steps[direction]];
        float high = max(a, b);
        float low = min(a, b);

        neighbours += (a + b);
        above += (((center - high) > (SdfResolveFeatureContrast * center)) ? 1u : 0u);
        below += (((low - center) > (SdfResolveFeatureContrast * low)) ? 1u : 0u);
    }
    contrast = abs(center - (neighbours * 0.125));

    return ((above >= 3u) || (below >= 3u));
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
            uint word = ((SDF_HISTORY_SURFACE_WORDS * ((texel.y * dims.x) + texel.x)) + 2u);

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
// A render sample's transport beside the color tap read at the same pixel: zero where that tap has no coverage, and for
// a sample with no surface.
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
void sdfResolveSpatial(uint2 pixel, float2 position, bool exact, uint2 render, float3 origin, float3 direction, out float4 color, out float4 transport, out uint2 word) {
    uint4 current = uint4(0u, 0u, render);

    if (exact) {
        SdfVisibility visibility = sdfLoadVisibility(sdfVisibilityRecord(pixel, 0u, render));
        float t = (sdfVisibilityHit(visibility) ? visibility.t : 0.0);

        color = puckReconstructionTapWithin(currentColor, int2(pixel), render, uint2(0, 0), current);
        transport = sdfSampleTransport(color.a, t, origin, direction);
        word = sdfTransportSampleWord(t);
        return;
    }

    PuckReconstructionFootprint footprint = puckReconstructionFootprintAt(position, render, passGroup.upscaleSharpness);
    PuckReconstructionSum colors = puckReconstructionSumStart();
    PuckReconstructionSum transports = puckReconstructionSumStart();

    [unroll] for (uint tap = 0u; tap < 16u; tap++) {
        float4 tapColor = float4(0.0, 0.0, 0.0, 0.0);
        float4 tapTransport = float4(0.0, 0.0, 0.0, 0.0);

        if (puckReconstructionReads(footprint, tap)) {
            int2 at = (footprint.origin + puckReconstructionTapOffset(tap));

            tapColor = puckReconstructionTapWithin(currentColor, at, render, uint2(0, 0), current);
            tapTransport = sdfResolveTransportAt(at, render, tapColor.a, origin, direction);
        }
        puckReconstructionAdd(footprint, tap, tapColor, colors);
        puckReconstructionAdd(footprint, tap, tapTransport, transports);
    }
    color = puckReconstructionFinish(footprint, colors);
    transport = puckReconstructionFinish(footprint, transports);
    word = sdfPackTransport(transport);
}

// The temporal path: this frame's 3x3 samples joined by the history the pixel's motion reaches, which it writes back.
// `accepted` says the history survived, `total` is the weight the mean `accumulated` (and its transport) gathered, and
// `reactive` is the reactivity of the tap nearest the pixel's center: the pixel's own, so a steady pixel beside a reactive
// one keeps its history.
void sdfResolveTemporal(uint3 id, uint2 extent, uint2 render, float2 jitter, float3 airOrigin, float3 airDirection, out bool accepted, out float total, out float reactive, out float4 accumulated, out float4 accumulatedTransport) {
    float2 scale = (float2(extent) / float2(render));
    float2 center = ((float2(id.xy) + 0.5) / scale);
    int2 nearest = int2(floor(center - jitter));
    float4 sum = float4(0.0, 0.0, 0.0, 0.0);
    float4 transportSum = float4(0.0, 0.0, 0.0, 0.0);
    float weightSum = 0.0;

    reactive = 0.0;
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
    // The 5x5 render samples about the nearest one, their luma for the feature test and the inner nine's colors.
    float window[25];
    float4 colors[9];

    [unroll] for (int wy = 0; wy < 5; wy++) {
        [unroll] for (int wx = 0; wx < 5; wx++) {
            float4 color = currentColor.Load(int3(sdfResolveClamp((nearest + int2(wx - 2, wy - 2)), render), 0));

            window[(wy * 5) + wx] = sdfToYCoCg(color.rgb).x;
            if ((wx >= 1) && (wx <= 3) && (wy >= 1) && (wy <= 3)) {
                colors[((wy - 1) * 3) + (wx - 1)] = color;
            }
        }
    }
    // Each 3x3 tap's surface identity (0 on a miss) and ray distance, and the receiver the history keeps for this pixel:
    // the tap nearest its center that is not a feature, so a feature the jitter leaves next frame does not reject a
    // history the surface under it carries. Validating against every tap keeps an edge's history whichever side of it the
    // jitter puts the nearest-depth tap on; the clip below still bounds what the history contributes. The feature the
    // pixel shows this frame is the most contrasting of its unreactive feature taps.
    uint identities[9];
    float distances[9];
    float centerWeight = -1.0;
    uint centerIdentity = 0u;
    float centerT = 0.0;
    float nearestWeight = -1.0;
    uint nearestIdentity = 0u;
    float nearestT = 0.0;
    bool sighted = false;
    float sightedContrast = 0.0;
    float3 sightedYCoCg = float3(0.0, 0.0, 0.0);

    [unroll] for (int dy = -1; dy <= 1; dy++) {
        [unroll] for (int dx = -1; dx <= 1; dx++) {
            uint2 pixel = sdfResolveClamp((nearest + int2(dx, dy)), render);
            uint tap = ((uint)((dy + 1) * 3) + (uint)(dx + 1));
            float4 color = colors[tap];
            float2 offset = (((float2(pixel) + 0.5 + jitter) - center) * scale);
            float weight = exp(-SdfResolveFilterFalloff * dot(offset, offset));
            float3 ycocg = sdfToYCoCg(color.rgb);
            float4 transport = float4(0.0, 0.0, 0.0, 0.0);
            float contrast;
            bool feature = sdfResolveFeature(window, (uint)(((dy + 2) * 5) + (dx + 2)), contrast);

            float tapReactive = reactivity[sdfReactivityIndex(pixel, 0u, render)];

            uint record = sdfVisibilityRecord(pixel, 0u, render);
            SdfVisibility visibility = sdfLoadVisibility(record);
            bool surface = sdfVisibilityHit(visibility);

            if (surface) {
                transport = sdfSampleTransport(color.a, visibility.t, airOrigin, airDirection);
            }

            identities[tap] = (surface ? visibility.identity : 0u);
            distances[tap] = (surface ? visibility.t : 0.0);
            if (weight > nearestWeight) {
                nearestWeight = weight;
                reactive = saturate(tapReactive);
                nearestIdentity = identities[tap];
                nearestT = distances[tap];
            }
            if (!feature && (weight > centerWeight)) {
                centerWeight = weight;
                centerIdentity = identities[tap];
                centerT = distances[tap];
            }
            if (feature && (tapReactive <= 0.0) && (contrast > sightedContrast)) {
                sighted = true;
                sightedContrast = contrast;
                sightedYCoCg = ycocg;
            }
            if (surface && (!hit || (visibility.t < hitT))) {
                hit = true;
                hitT = visibility.t;
                hitIdentity = visibility.identity;
                hitRecord = record;
                hitPixel = pixel;
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
    if (centerWeight < 0.0) {
        centerIdentity = nearestIdentity;
        centerT = nearestT;
    }

    accepted = false;
    float2 historyPosition = float2(0.0, 0.0);
    float historyWeightGathered = 0.0;
    // The feature the history remembers for this pixel, with the frames it has left, and how far, in output pixels, the
    // history moved to reach the pixel.
    float3 featureYCoCg = float3(0.0, 0.0, 0.0);
    uint featureLife = 0u;
    float travel = 1.0e9;

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
            travel = length(motion * float2(extent));
            if (all(historyPosition >= -0.5) && all(historyPosition <= (float2(extent) - 0.5))) {
                uint2 texel = sdfResolveClamp(int2(floor(historyPosition + 0.5)), extent);
                uint word = (SDF_HISTORY_SURFACE_WORDS * ((texel.y * extent.x) + texel.x));
                uint historyIdentity = historySurface[word];
                uint distanceAndWeight = historySurface[word + 1u];
                float historyT = f16tof32(distanceAndWeight & 0xFFFFu);
                uint featureWordA = historySurface[word + 4u];
                uint featureWordB = historySurface[word + 5u];

                // A tap's distance in the preceding view: its own, shifted by the camera's move along the nearest hit.
                float shift = (hit ? (previousT - hitT) : 0.0);

                historyWeightGathered = f16tof32(distanceAndWeight >> 16u);
                featureYCoCg = float3(f16tof32(featureWordA & 0xFFFFu), f16tof32(featureWordA >> 16u), f16tof32(featureWordB & 0xFFFFu));
                featureLife = (featureWordB >> 16u);
                [unroll] for (uint tap = 0u; tap < 9u; tap++) {
                    accepted = (accepted || ((identities[tap] == 0u)
                        ? (historyIdentity == 0u)
                        : sdfHistoryReceiverMatches(identities[tap], historyIdentity, (distances[tap] + shift), historyT)));
                }
            }
        }
    }

    // The weighted mean of every sample the pixel has gathered: history clipped to this frame's neighbourhood, widened
    // to the feature the pixel remembers while its surface stands still, and weighed down by reactivity, then this
    // frame's samples, the transport with the color's weights. While less than one sample's full weight is gathered,
    // the spatial path shows through.
    float historyWeight = 0.0;
    float4 history = float4(0.0, 0.0, 0.0, 0.0);
    float4 historyTransport = float4(0.0, 0.0, 0.0, 0.0);
    float weightCap = SDF_HISTORY_WEIGHT_CAP;

    if (accepted) {
        float4 previous;
        float4 previousTransport;

        sdfHistoryAt(historyPosition, extent, previous, previousTransport);
        // Clipping infinity can produce NaN, and even zero history weight cannot remove it (NaN * 0 is NaN).
        accepted = (all(isfinite(previous)) && all(isfinite(previousTransport)));
        if (accepted) {
            bool remembered = (hit && (featureLife != 0u) && (travel <= SdfResolveFeatureTravel) && all(isfinite(featureYCoCg)));
            float3 clipMin = (remembered ? min(boxMin, featureYCoCg) : boxMin);
            float3 clipMax = (remembered ? max(boxMax, featureYCoCg) : boxMax);

            weightCap = (remembered ? SDF_HISTORY_FEATURE_WEIGHT_CAP : SDF_HISTORY_WEIGHT_CAP);
            historyWeight = (clamp(historyWeightGathered, 0.0, weightCap) * (1.0 - reactive));
            history = float4(
                sdfFromYCoCg(sdfClipToBox(sdfToYCoCg(max(previous.rgb, 0.0)), clipMin, clipMax)),
                clamp(previous.a, coverageMin, coverageMax)
            );
            historyTransport = clamp(previousTransport, transportMin, transportMax);
        }
    }

    total = (historyWeight + weightSum);
    accumulated = (((history * historyWeight) + sum) / max(total, 1.0e-6));
    accumulatedTransport = (((historyTransport * historyWeight) + transportSum) / max(total, 1.0e-6));
    uint word = (SDF_HISTORY_SURFACE_WORDS * ((id.y * extent.x) + id.x));
    // The working history is half-float. A non-finite current sample contributes no reusable history, and a finite
    // reconstruction must remain representable when stored, so one bright transient cannot poison later frames.
    bool reusable = (all(isfinite(accumulated)) && all(isfinite(accumulatedTransport)) && isfinite(total));
    // The feature the pixel carries on: this frame's sighting for a whole period, or the accepted history's with one
    // frame less, which the jitter comes round to before it runs out; a restarted history remembers none.
    float3 writtenFeature = (sighted ? sightedYCoCg : featureYCoCg);
    uint writtenLife = (sighted ? SDF_TEMPORAL_PERIOD : ((accepted && (featureLife != 0u)) ? (featureLife - 1u) : 0u));

    if (!reusable || !all(isfinite(writtenFeature))) {
        writtenLife = 0u;
    }
    historyColorRW[id.xy] = (reusable ? clamp(accumulated, -65504.0, 65504.0) : float4(0.0, 0.0, 0.0, 0.0));
    historySurfaceRW[word] = centerIdentity;
    historySurfaceRW[word + 1u] = (f32tof16(centerT) | (f32tof16(reusable ? min(total, weightCap) : 0.0) << 16u));
    uint2 historyTransportWords = sdfPackTransport(reusable ? accumulatedTransport : float4(0.0, 0.0, 0.0, 0.0));

    historySurfaceRW[word + 2u] = historyTransportWords.x;
    historySurfaceRW[word + 3u] = historyTransportWords.y;
    historySurfaceRW[word + 4u] = (f32tof16(writtenFeature.x) | (f32tof16(writtenFeature.y) << 16u));
    historySurfaceRW[word + 5u] = (f32tof16(writtenFeature.z) | (writtenLife << 16u));
}

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint2 extent = passGroup.extent;
    if (any(id.xy >= extent)) return;
    uint2 render = passGroup.imageExtent;
    float2 jitter = passGroup.jitter;
    bool temporal = ((passGroup.temporal != 0u) && sdfDebugPreservesScene(passGroup.debugMode));
    // The spatial path at this frame's sample grid: the sample of render pixel i lies at i + 0.5 + jitter.
    bool unjittered = (!temporal || all(jitter == 0.0));
    float3 airOrigin = passGroup.viewPosition;
    float3 airDirection = sdfResolveAirDirection(id.xy, extent);
    bool accepted = false;
    float total = 0.0;
    float reactive = 0.0;
    float4 accumulated = float4(0.0, 0.0, 0.0, 0.0);
    float4 accumulatedTransport = float4(0.0, 0.0, 0.0, 0.0);

    if (temporal) {
        sdfResolveTemporal(id, extent, render, jitter, airOrigin, airDirection, accepted, total, reactive, accumulated, accumulatedTransport);
    }

    // The spatial path shows where the history is rejected or holds less than one sample's weight, and by its
    // reactivity where the pixel is reactive; a pixel whose gathered weight covers it shows the mean alone and
    // reconstructs nothing. A reactive pixel gathers this frame's samples alone, whose weight swings about one sample's
    // as the jitter moves, so it takes the spatial path whatever it gathered (weighing the two by that weight would
    // switch a whole reactive screen between the two filters frame by frame), and it takes it on the unjittered grid, as
    // a view with no temporal path does: a screen reads its image along the unjittered ray, so that sample holds still.
    // Reactivity is the pixel's own sample's, so a jittered edge beside a screen keeps its history.
    float shown = (accepted ? (saturate(total) * (1.0 - reactive)) : 0.0);
    bool still = (unjittered || (reactive > 0.0));
    float4 resolved = accumulated;
    uint2 transportWord = sdfPackTransport(accumulatedTransport);

    if (shown < 1.0) {
        float4 spatial;
        float4 spatialTransport;
        uint2 spatialWord;

        sdfResolveSpatial(id.xy, (((((float2(id.xy) + 0.5) * float2(render)) / float2(extent)) - 0.5) - (still ? float2(0.0, 0.0) : jitter)),
            (still && all(render == extent)), render, airOrigin, airDirection, spatial, spatialTransport, spatialWord);
        resolved = lerp(spatial, accumulated, shown);
        transportWord = ((shown > 0.0) ? sdfPackTransport(lerp(spatialTransport, accumulatedTransport, shown)) : spatialWord);
    }

    output[id.xy] = resolved;
    sdfWorkTexels = 1u;
    transportRW[((id.y * extent.x) + id.x)] = transportWord;
    puckCountWork(sdfWorkSteps, sdfWorkTexels);
}
