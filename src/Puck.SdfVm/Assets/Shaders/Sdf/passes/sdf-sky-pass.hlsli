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
#include "../isa/sdf-sky.interface.hlsli"
#include "../frame/sdf-viewport.hlsli"
#include "../frame/sdf-visibility.hlsli"
#include "../frame/sdf-work.hlsli"
#include "../shade/sdf-sky.hlsli"
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
// The transport of the surface share a pixel shows, of coverage `coverage` (sdf-transport.hlsli): its fog's in-scatter
// weight, at most its coverage, and the ray distance its media are clipped at, zero where it shows no surface. A pixel
// that is one render sample, every pixel of a native view and each pixel a resolve copied whole (the first frame of a
// temporal epoch at its render grid's extent among them), takes only that sample's ray distance from its source and
// derives the rest at the one site below that both reach, so a resolved copy of a sample composites with exactly the
// arithmetic its native view runs. A reconstruction's transport is the resolve's.
void sdfSkyPassSurface(uint2 pixel, float coverage, out float fog, out float distance) {
    bool sample = true;
    float t = 0.0;
    uint word = 0u;

    if (passGroup.resolvedSurface != 0u) {
        word = transport[((pixel.y * passGroup.extent.x) + pixel.x)];
        sample = sdfTransportIsSample(word);
        t = sdfTransportSampleDistance(word);
    } else if (SDF_VISIBILITY_CURRENT(pixel, cullBounds)) {
        SdfVisibility visibility = sdfLoadVisibility(sdfVisibilityRecord(pixel, 0u, passGroup.imageExtent));

        t = (sdfVisibilityHit(visibility) ? visibility.t : 0.0);
    }

    float2 surface = (sample ? sdfSampleTransport(coverage, t) : sdfUnpackTransport(word));

    fog = clamp(surface.x, 0.0, coverage);
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
// own texel and reads it alone.
bool sdfSkyPassRuns(uint2 pixel, out float3 base, out float3 scale, out float3 offset) {
    int2 grid = int2(passGroup.imageExtent);
    float2 position = ((((float2(pixel) + 0.5) * float2(grid)) / float2(passGroup.extent)) - 0.5);
    int2 origin = int2(floor(position));
    float2 fraction = (position - float2(origin));
    float total = 0.0;

    base = float3(0.0, 0.0, 0.0);
    scale = float3(0.0, 0.0, 0.0);
    offset = float3(0.0, 0.0, 0.0);
    [unroll] for (uint i = 0u; i < 4u; i++) {
        int2 corner = int2((int)(i & 1u), (int)(i >> 1u));
        float weight = (lerp((1.0 - fraction.x), fraction.x, (float)corner.x) * lerp((1.0 - fraction.y), fraction.y, (float)corner.y));

        if (weight > 0.0) {
            int3 tap = int3(clamp((origin + corner), int2(0, 0), (grid - 1)), 0);
            float4 runBase = skyBase.Load(tap);

            // Only the base is written for an invalid tap. Do not load its unwritten scale or offset: multiplying an
            // undefined value by zero does not exclude it from the filter (zero times NaN is still NaN).
            if (runBase.a <= 0.0) {
                continue;
            }
            weight *= runBase.a;
            base += (weight * runBase.rgb);
            scale += (weight * skyScale.Load(tap).rgb);
            offset += (weight * skyOffset.Load(tap).rgb);
            total += weight;
        }
    }
    if (total <= 0.0) {
        return false;
    }
    base /= total;
    scale /= total;
    offset /= total;

    return true;
}
// The sky the fog in-scatters in a direction: the residency's environment map (sdfSkyEnvironment), the gradient with no
// body, filtered bilinearly over the four texels about the direction. It evaluates no sky.
float3 sdfSkyPassEnvironment(float3 direction) {
    uint taps[4];
    float weights[4];
    float3 color = float3(0.0, 0.0, 0.0);

    sdfSkyEnvironmentTaps(direction, taps, weights);
    [unroll] for (uint i = 0u; i < 4u; i++) {
        color += (weights[i] * sdfSkyEnvironmentUnpack(sdfSkyEnvironment[taps[i]]));
    }

    return color;
}
// The pixel's unjittered sky direction.
float3 sdfSkyPassDirection(ViewportData view, uint2 pixel) {
    return cameraRayDirection(view, ((float2(pixel) + 0.5) / float2(passGroup.extent)));
}
#endif
