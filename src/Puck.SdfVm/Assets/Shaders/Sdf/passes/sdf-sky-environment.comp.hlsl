// The sky's environment map, one invocation a texel: the sky's gradient at the texel's centre direction, the layer the
// fog in-scatters, packed as four half floats (sdf-sky-environment.hlsli). No body, star or cloud enters it. The
// residency's upload dispatches it, then the reduction (sdf-sky-environment-reduce.comp.hlsl), only on an upload whose
// gradient differs from the one the map holds while the fog reads it (SdfWorldTables.SkyEnvironment.cs), so a still sky
// renders it once. Each texel counts one sky evaluation (gpu.sky.evaluations) and one texel written.
#include "../isa/sdf-sky-environment.interface.hlsli"
#include "../frame/sdf-work.hlsli"
#include "../shade/sdf-sky.hlsli"
#include "../shade/sdf-sky-environment.hlsli"

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= passGroup.extent)) {
        return;
    }

    uint evaluations = 0u;

    sdfSkyEnvironmentRW[((id.y * (uint)SdfSkyEnvironmentSize) + id.x)] = sdfSkyEnvironmentPack(sdfSkyGradient(sdfSkyEnvironmentDirection(id.xy)));
    evaluations += 1u;
    sdfWorkTexels = 1u;

    puckCountWork(sdfWorkSteps, sdfWorkTexels);
    puckCountSky(evaluations);
}
