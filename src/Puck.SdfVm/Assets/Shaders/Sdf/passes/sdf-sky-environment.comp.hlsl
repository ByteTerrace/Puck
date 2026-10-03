// The sky's environment map, one invocation a texel: the layers the lighting sees at the texel's centre direction, but a
// disc, composed over black in their authored order (sdfSkyEnvironmentColor), packed as four half floats
// (sdf-sky-environment.hlsli). The residency's upload dispatches it, then the reduction
// (sdf-sky-environment-reduce.comp.hlsl), only on an upload whose lit layers differ from the ones the map holds while the
// fog reads it (SdfWorldTables.SkyEnvironment.cs), so a still sky renders it once. Each layer counts its own evaluations
// (gpu.sky.evaluations) in its detail row, and each texel one texel written.
#include "../isa/sdf-sky-environment.interface.hlsli"
#include "../frame/sdf-work.hlsli"
#include "../sky/sdf-sky.hlsli"
#include "../shade/sdf-sky-environment.hlsli"

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= passGroup.extent)) {
        return;
    }

    sdfSkyEnvironmentRW[((id.y * (uint)SdfSkyEnvironmentSize) + id.x)] = sdfSkyEnvironmentPack(sdfSkyEnvironmentColor(sdfSkyEnvironmentDirection(id.xy)));
    sdfWorkTexels = 1u;

    puckCountWork(sdfWorkSteps, sdfWorkTexels);
}
