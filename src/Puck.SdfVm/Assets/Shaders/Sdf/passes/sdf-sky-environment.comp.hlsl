// The sky's environment map, one invocation a texel: the layers the lighting sees at the texel's centre direction, but a
// disc, composed over black in their authored order (sdfSkyEnvironmentColor), packed as four half floats
// (sdf-sky-environment.hlsli). The residency's graph producer dispatches it after acquiring its panorama reads, then
// the reduction (sdf-sky-environment-reduce.comp.hlsl). Its retained map follows the actual lighting-visible layers
// and acquired image publications, so a held sky renders once. Each layer counts its own evaluations
// (gpu.sky.evaluations) in its detail row, and each texel one texel written.
#include "../isa/sdf-sky-environment.interface.hlsli"
#include "../isa/sdf-isa.hlsli"
#include "../frame/sdf-work.hlsli"
#include "../frame/sdf-screen-mapping.hlsli"
#define SDF_SKY_SCREENS
#include "../sky/sdf-sky.hlsli"
#include "../shade/sdf-sky-environment.hlsli"

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= passGroup.extent)) {
        return;
    }

    uint texel = id.y * (uint)SdfSkyEnvironmentSize + id.x;
    float3 reflection;
    float3 color = sdfSkyEnvironmentColor(sdfSkyEnvironmentDirection(id.xy), reflection);
    sdfSkyEnvironmentRW[texel] = sdfSkyEnvironmentPack(color);
    sdfSkyEnvironmentRW[texel + (uint)(SdfSkyEnvironmentSize * SdfSkyEnvironmentSize)] = sdfSkyEnvironmentPack(reflection);
    sdfWorkTexels = 2u;

    puckCountWork(sdfWorkSteps, sdfWorkTexels);
}
