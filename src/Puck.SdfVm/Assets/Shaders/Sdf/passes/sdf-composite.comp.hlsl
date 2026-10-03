// The composite, one invocation an output pixel, the last pass of a view: its color, from the lit image, its surface
// transport and the sky's runs, in the order the stack is authored and the air lies. The lit image is premultiplied by
// its coverage and by each sample's fog transmittance (sdf-transport.hlsli), so the pixel is two shares: the surface
// share, of the lit image's coverage, and the sky share, the rest.
// - The fog's in-scatter is the sky's gradient in the pixel's direction, read from the residency's environment map
//   (sdfSkyPassEnvironment), scaled by the transport's in-scatter weight, the coverage-weighted one minus
//   transmittance of the samples the lit image was filtered from, so an edge fogs as its covered share. The fog
//   evaluates no sky, and a zero weight, which a zero fog density gives, reads no map.
// - Where the coverage is below one, the sky's layers compose into the sky share in their authored order
//   (sdfSkyCompose): the lowest field run's offset, then each point layer evaluated here at the pixel and each upper field
//   run's scale and offset. The field runs are read from the grid the sky evaluated them on, filtered over the texels it
//   evaluated; where it evaluated none beside the pixel, the composite evaluates every field layer here, and each counts
//   its own evaluation. A wholly covered pixel reads no run.
// - The bounded volumes composite last over each share, the surface share's clipped at the transport's distance and the
//   sky share's at the far distance, from the camera's near plane, so a medium never paints through solid geometry,
//   even at an edge whose samples it lies behind.
// A debug view's lit image is its whole picture, so it passes through.
#include "sdf-sky-pass.hlsli"
#include "../shade/shade-volumes.hlsli"

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= passGroup.extent)) {
        return;
    }

    float4 litColor = sdfSkyPassLit(int2(id.xy));
    float3 color = litColor.rgb;

    if (passGroup.debugMode == 0u) {
        ViewportData view = sdfSkyPassView();
        float3 direction = sdfSkyPassDirection(view, id.xy);
        float coverage = litColor.a;
        float fog;
        float t;
        float3 surface = litColor.rgb;
        float3 sky = float3(0.0, 0.0, 0.0);

        sdfSkyPassSurface(id.xy, coverage, fog, t);
        if (fog > 0.0) {
            surface += (sdfSkyPassEnvironment(direction) * fog);
        }
        if (coverage < 1.0) {
            float3 base;
            float3 scales[SDF_SKY_MAX_UPPER_FIELD_RUNS];
            float3 offsets[SDF_SKY_MAX_UPPER_FIELD_RUNS];
            bool summarized = sdfSkyPassRuns(id.xy, base, scales, offsets);

            sky = ((1.0 - coverage) * sdfSkyCompose(direction, summarized, base, scales, offsets));
        }

        float far = worldFarDistance(view);
        float covered;

        color = shadeVolumes(surface, coverage, ((t > 0.0) ? t : far), sky, view.position.xyz, direction, worldRayDistanceAt(view, direction, worldNearDistance(view)), far, id.xy, covered);
    }

    // The float working color; the display encode dithers and quantizes it.
    output[id.xy] = float4(color, 1.0);
    sdfWorkTexels = 1u;
    puckCountWork(sdfWorkSteps, sdfWorkTexels);
}
