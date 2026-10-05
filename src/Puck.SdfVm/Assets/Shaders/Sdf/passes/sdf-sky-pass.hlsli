// What the sky's field runs (sdf-sky-runs.comp.hlsl) and the composite (sdf-composite.comp.hlsl) share: the sky interface
// (SdfWorldPackage.SkyMembers), one invocation a pixel of the pass's extent, and the reads of what the views or the
// resolve left for them. The sky runs on the render grid and reads the color views wrote, and a native view's composite
// reads views' lit image and visibility records: each current only inside the dispatch box cull-args wrote, outside which
// the beam proved every tile empty, so a pixel there reads as a miss. A reduced or temporal view's composite reads the
// resolve's lit image and transport at the output extent, written for every pixel (passGroup.resolvedSurface). A pixel's
// sky direction is its unjittered one, so the sky never moves with a temporal view's samples.
#ifndef PASSES_SDF_SKY_PASS_HLSLI
#define PASSES_SDF_SKY_PASS_HLSLI
#define SDF_DYNAMIC_TRANSFORMS
// The screens a panorama layer or a textured disc samples (sky/kinds/panorama.hlsli, sky/kinds/disc.hlsli).
#define SDF_SCREEN_SOURCES
#define SDF_SKY_SCREENS
#define SDF_SKY_VIEWS
#include "../isa/sdf-sky.interface.hlsli"
#include "../frame/sdf-viewport.hlsli"
#include "../frame/sdf-visibility.hlsli"
#include "../frame/sdf-work.hlsli"
#include "../frame/sdf-debug-modes.hlsli"
#include "../frame/sdf-environment.hlsli"
#include "../sky/sdf-sky.hlsli"
#include "../shade/sdf-sky-environment.hlsli"
#include "../shade/sdf-transport.hlsli"

// Whether the lit image and the surface transport at a pixel were written this frame.
bool sdfSkyPassCurrent(uint2 pixel) {
    return ((passGroup.resolvedSurface != 0u) || SDF_VISIBILITY_CURRENT(pixel, cullBounds));
}
// The lit image at a pixel, clamped to the output: the color premultiplied by its coverage, the coverage in alpha, and
// nothing where the pixel was not written this frame.
float4 sdfSkyPassLit(int2 pixel) {
    uint2 clamped = (uint2)clamp(pixel, int2(0, 0), (int2(passGroup.extent) - 1));

    return (sdfSkyPassCurrent(clamped) ? lit.Load(int3(clamped, 0)) : float4(0.0, 0.0, 0.0, 0.0));
}
// The transport of the surface share a pixel shows, of coverage `coverage`, along its ray from `origin` along `direction`
// (sdf-transport.hlsli): each atmosphere kind's in-scatter weight, fog in x, haze in y and medium in z, each at most its
// coverage, and the ray distance its media are clipped at, zero where it shows no surface. A pixel that is one render
// sample, every pixel of a native view and each pixel a resolve copied whole (the first frame of a temporal epoch at its
// render grid's extent among them), takes only that sample's ray distance from its source and derives the rest at the one
// site below that both reach, so a resolved copy of a sample composites with exactly the arithmetic its native view runs.
// A reconstruction's transport is the resolve's.
void sdfSkyPassSurface(uint2 pixel, float coverage, float3 origin, float3 direction, out float3 weights, out float distance) {
    bool sample = true;
    float t = 0.0;
    uint2 word = uint2(0u, 0u);

    if (passGroup.resolvedSurface != 0u) {
        word = transport[((pixel.y * passGroup.extent.x) + pixel.x)];
        sample = sdfTransportIsSample(word);
        t = sdfTransportSampleDistance(word);
    } else if (SDF_VISIBILITY_CURRENT(pixel, cullBounds)) {
        SdfVisibility visibility = sdfLoadVisibility(sdfVisibilityRecord(pixel, 0u, passGroup.imageExtent));

        t = (sdfVisibilityHit(visibility) ? visibility.t : 0.0);
    }

    float4 surface = (sample ? sdfSampleTransport(coverage, t, origin, direction) : sdfUnpackTransport(word));

    weights = clamp(surface.xzw, 0.0, coverage);
    distance = (sample ? ((surface.y > 0.0) ? t : 0.0) : sdfTransportDistance(coverage, surface));
}
// The pixel's view without the sample's jitter.
ViewportData sdfSkyPassView() {
    ViewportData view = worldView();

    view.lens.yz = passGroup.frustumOffset;

    return view;
}
// The sky's field runs at a pixel of the pass's extent, filtered from the render grid (passGroup.imageExtent) the sky
// evaluated them on: the bilinear taps beside the pixel, each weighted by whether the sky evaluated it, so an unevaluated
// texel never darkens the sky. False when the sky evaluated none of them. On a native view's grid every pixel lands on its
// own texel and reads it alone. The base is read at every tap and counts its load in the lowest run's row; each upper run
// reads the upper images holding its six half floats (its scale, then its offset) and counts them in its own row.
bool sdfSkyPassRuns(uint2 pixel, out float3 base, out float3 scales[SDF_SKY_MAX_UPPER_FIELD_RUNS], out float3 offsets[SDF_SKY_MAX_UPPER_FIELD_RUNS]) {
    int2 grid = int2(passGroup.skyFieldExtent);
    float2 position = ((((float2(pixel) + 0.5) * float2(grid)) / float2(passGroup.extent)) - 0.5);
    int2 origin = int2(floor(position));
    float2 fraction = (position - float2(origin));
    uint upper = min(sdfSky[0].UpperRuns, SDF_SKY_MAX_UPPER_FIELD_RUNS);
    float total = 0.0;

    base = float3(0.0, 0.0, 0.0);
    [unroll] for (uint run = 0u; (run < SDF_SKY_MAX_UPPER_FIELD_RUNS); run++) {
        scales[run] = float3(0.0, 0.0, 0.0);
        offsets[run] = float3(0.0, 0.0, 0.0);
    }
    [unroll] for (uint i = 0u; i < 4u; i++) {
        int2 corner = int2((int)(i & 1u), (int)(i >> 1u));
        float weight = (lerp((1.0 - fraction.x), fraction.x, (float)corner.x) * lerp((1.0 - fraction.y), fraction.y, (float)corner.y));

        if (weight > 0.0) {
            int3 tap = int3(clamp((origin + corner), int2(0, 0), (grid - 1)), 0);
            float4 runBase = skyBase.Load(tap);
            sdfCountSky(0u, 0u, 0u, 0u, 0u, 1u);

            // Only the base is written for an invalid tap. Do not load its unwritten upper runs: multiplying an undefined
            // value by zero does not exclude it from the filter (zero times NaN is still NaN).
            if (runBase.a <= 0.0) {
                continue;
            }
            weight *= runBase.a;
            base += (weight * runBase.rgb);
            if (upper > 0u) {
                float4 upper0 = skyUpper0.Load(tap);
                float4 upper1 = skyUpper1.Load(tap);

                sdfCountSky(1u, 0u, 0u, 0u, 0u, 2u);
                scales[0] += (weight * upper0.xyz);
                offsets[0] += (weight * float3(upper0.w, upper1.xy));
                if (upper > 1u) {
                    float4 upper2 = skyUpper2.Load(tap);

                    sdfCountSky(2u, 0u, 0u, 0u, 0u, 1u);
                    scales[1] += (weight * float3(upper1.zw, upper2.x));
                    offsets[1] += (weight * upper2.yzw);
                }
            }
            total += weight;
        }
    }
    if (total <= 0.0) {
        return false;
    }
    base /= total;
    if (passGroup.debugMode == DebugViewModeSkyCost) {
        sdfSkyCost += base;
        base = float3(0.0, 0.0, 0.0);
    }
    [unroll] for (uint summary = 0u; (summary < SDF_SKY_MAX_UPPER_FIELD_RUNS); summary++) {
        scales[summary] = ((summary < upper) ? (scales[summary] / total) : float3(1.0, 1.0, 1.0));
        offsets[summary] = ((summary < upper) ? (offsets[summary] / total) : float3(0.0, 0.0, 0.0));
    }

    return true;
}
// The sky the fog and the haze in-scatter in a direction: the residency's environment map (sdfSkyEnvironment), the
// layers the lighting sees with no body, filtered bilinearly over the four texels about the direction. It evaluates no
// sky.
float3 sdfSkyPassEnvironment(float3 direction) {
    uint taps[4];
    float weights[4];
    float3 color = float3(0.0, 0.0, 0.0);

    sdfSkyEnvironmentTaps(direction, taps, weights);
    [unroll] for (uint i = 0u; i < 4u; i++) {
        color += (weights[i] * sdfSkyEnvironmentUnpack(sdfSkyEnvironment[taps[i]]));
    }
    sdfCountSky(SDF_SKY_DETAIL_ATMOSPHERE, 0u, 0u, 0u, 0u, 4u);

    return color;
}
// The pixel's unjittered sky direction.
float3 sdfSkyPassDirection(ViewportData view, uint2 pixel) {
    return cameraRayDirection(view, ((float2(pixel) + 0.5) / float2(passGroup.extent)));
}
#endif
