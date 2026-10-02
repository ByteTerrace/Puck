// The composite, one invocation an output pixel, the last pass of a view: its color, from the lit image and the sky's
// runs, in the order the stack is authored and the air lies.
// - The lit image is fogged first: its premultiplied color blends toward the sky's gradient in the pixel's direction by
//   the fog over its ray distance, the gradient scaled by its coverage, so an edge fogs as its covered share.
// - Where the coverage is below one, the sky's runs compose beneath it: the gradient's offset, then the point run (the
//   disc and the stars) evaluated here at the pixel, then the cloud run's scale and offset, and the lit color over the
//   result by its coverage. A wholly covered pixel reads no run and evaluates no layer.
// - The bounded volumes composite last, clipped to the span from the camera's near plane to the surface's ray distance,
//   or to the far distance on a miss, so a medium never paints through solid geometry.
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
        float t = sdfSkyPassSurfaceDistance(id.xy);

        if ((litColor.a > 0.0) && (t > 0.0)) {
            float fog = (1.0 - exp(-sdfSky[0].FogDensity * t));

            color = lerp(color, (sdfSkyGradient(direction) * litColor.a), fog);
        }
        if (litColor.a < 1.0) {
            float3 sky = skyBase.Load(int3(id.xy, 0)).rgb;

            sky += sdfSkyPoints(direction);
            sky = ((skyScale.Load(int3(id.xy, 0)).rgb * sky) + skyOffset.Load(int3(id.xy, 0)).rgb);
            color += ((1.0 - litColor.a) * sky);
        }

        float covered;

        color = shadeVolumes(color, view.position.xyz, direction, worldRayDistanceAt(view, direction, worldNearDistance(view)), ((t > 0.0) ? t : worldFarDistance(view)), id.xy, covered);
    }

    // The float working color; the display encode dithers and quantizes it.
    output[id.xy] = float4(color, 1.0);
    sdfWorkTexels = 1u;
    puckCountWork(sdfWorkSteps, sdfWorkTexels);
}
