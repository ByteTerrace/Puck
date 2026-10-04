// The composite, one invocation an output pixel, the last pass of a view: its color, from the lit image, its surface
// transport and the sky's runs, in the order the stack is authored and the air lies. The lit image is premultiplied by
// its coverage and by each sample's atmosphere transmittance (sdf-transport.hlsli), so the pixel is two shares: the
// surface share, of the lit image's coverage, and the sky share, the rest.
// - The atmosphere's in-scatter (sdf-atmosphere.hlsli) adds each kind's colour in the pixel's direction by the transport's
//   weight for it, the coverage-weighted in-scatter of the samples the lit image was filtered from, so an edge hazes as
//   its covered share: the fog's authored colour, or the sky the lighting sees read from the residency's environment map
//   (sdfSkyPassEnvironment); the haze's, that sky and the light-casting bodies' light by its phase; the medium's colour. A
//   zero weight evaluates nothing, and each kind evaluated at the pixel counts one evaluation in the atmosphere detail row
//   (SDF_SKY_DETAIL_ATMOSPHERE), so a pixel of an atmosphere that authors no kind counts none. The in-scatter evaluates no
//   sky.
// - Where the coverage is below one, the sky's layers compose into the sky share in their authored order
//   (sdfSkyCompose): the lowest field run's offset, then each point layer evaluated here at the pixel and each upper field
//   run's scale and offset. The field runs are read from the grid the sky evaluated them on, filtered over the texels it
//   evaluated; where it evaluated none beside the pixel, the composite evaluates every field layer here, and each counts
//   its own evaluation. The sky share then passes through the haze and the medium to the far distance; the fog ends at the
//   sky, which is its colour at infinity. A wholly covered pixel reads no run.
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
        float3 origin = view.position.xyz;
        float coverage = litColor.a;
        float3 weights;
        float t;
        float3 surface = litColor.rgb;
        float3 sky = float3(0.0, 0.0, 0.0);
        SdfAir skyAir;

        skyAir.transmittance = 1.0;
        skyAir.weights = float3(0.0, 0.0, 0.0);
        sdfSkyPassSurface(id.xy, coverage, origin, direction, weights, t);
        if ((coverage < 1.0) && sdfAirBeforeSky()) {
            skyAir = sdfAirAlong(origin, direction, worldFarDistance(view), false);
        }

        float3 kinds = max(weights, skyAir.weights);
        float3 ambient = (((kinds.y > 0.0) || ((kinds.x > 0.0) && sdfAirFogReadsSky())) ? sdfSkyPassEnvironment(direction) : float3(0.0, 0.0, 0.0));
        float3 hazeColor = float3(0.0, 0.0, 0.0);

        if (kinds.x > 0.0) {
            puckCountDetail(SDF_SKY_DETAIL_ATMOSPHERE, 0u, 0u, 1u, 0u, 0u);
            surface += ((sdfAirFogReadsSky() ? ambient : sdfSky[0].FogColor) * weights.x);
        }
        if (kinds.y > 0.0) {
            puckCountDetail(SDF_SKY_DETAIL_ATMOSPHERE, 0u, 0u, 1u, 0u, 0u);
            hazeColor = (ambient + sdfAirBodies(direction, sdfSky[0].HazeAnisotropy));
            surface += (hazeColor * weights.y);
        }
        if (kinds.z > 0.0) {
            puckCountDetail(SDF_SKY_DETAIL_ATMOSPHERE, 0u, 0u, 1u, 0u, 0u);
            surface += (sdfSky[0].MediumColor * weights.z);
        }
        if (coverage < 1.0) {
            float3 base;
            float3 scales[SDF_SKY_MAX_UPPER_FIELD_RUNS];
            float3 offsets[SDF_SKY_MAX_UPPER_FIELD_RUNS];
            bool summarized = sdfSkyPassRuns(id.xy, base, scales, offsets);

            sky = sdfSkyCompose(direction, summarized, base, scales, offsets);
            if (sdfAirBeforeSky()) {
                sky = ((skyAir.transmittance * sky) + (skyAir.weights.y * hazeColor) + (skyAir.weights.z * sdfSky[0].MediumColor));
            }
            sky = ((1.0 - coverage) * sky);
        }

        float far = worldFarDistance(view);
        float covered;

        color = shadeVolumes(surface, coverage, ((t > 0.0) ? t : far), sky, origin, direction, worldRayDistanceAt(view, direction, worldNearDistance(view)), far, id.xy, covered);
    }

    // The float working color; the display encode dithers and quantizes it.
    output[id.xy] = float4(color, 1.0);
    sdfWorkTexels = 1u;
    puckCountWork(sdfWorkSteps, sdfWorkTexels);
}
