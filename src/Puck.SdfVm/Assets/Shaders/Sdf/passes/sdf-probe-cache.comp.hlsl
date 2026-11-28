#include "../isa/sdf-probe-relight.interface.hlsli"
#include "../isa/sdf-render.hlsli"
// Each live allocation has one owner. A traced probe's hits are already in its slot (the trace job copied them there);
// this pass stamps the slot's token before selection or a relight reads it. The next pass reads only after the cache
// write/read barrier.
[numthreads(32, 1, 1)]
void CSMain(uint3 group : SV_GroupID, uint lane : SV_GroupIndex) {
    uint input = group.x * SDF_PROBE_RELIGHT_INPUT_WORDS;
    uint output = group.x * SDF_PROBE_RELIGHT_RESULT_WORDS;
    uint flags = sdfProbeInput[input];
    uint cache = sdfProbeInput[input + 3] * SDF_PROBE_CACHE_WORDS;
    bool kept = (flags & 12u) == 8u;
    for (uint word = lane; word < SDF_PROBE_RELIGHT_RESULT_WORDS; word += 32) {
        uint value = 0;
        if (word == SDF_PROBE_RELIGHT_MOVED_WORD) { value = 1; }
        else if (word >= SDF_PROBE_RELIGHT_OUTPUT_WORDS) {
            uint record = word - SDF_PROBE_RELIGHT_OUTPUT_WORDS;
            value = kept && record != 0 && record < SDF_MESH_IRRADIANCE_CELL_WORD ?
                sdfMeshIrradiance[sdfProbeInput[input + SDF_PROBE_RELIGHT_PREVIOUS_WORD] * SDF_MESH_IRRADIANCE_PROBE_WORDS + record] :
                sdfProbeInput[input + SDF_PROBE_RECORD_INPUT_WORD + record];
        }
        sdfProbeHitsRW[output + word] = value;
    }
    if ((sdfProbeInput[input] & 4u) == 0) { return; }
    if (lane < 2) { sdfProbeCacheRW[cache + lane] = sdfProbeInput[input + 1 + lane]; }
}
