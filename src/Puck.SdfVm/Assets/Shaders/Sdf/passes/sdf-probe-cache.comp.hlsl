#include "../isa/sdf-probe-relight.interface.hlsli"
#include "../isa/sdf-render.hlsli"
// Each live allocation has one owner. A traced probe's hits are already in its slot (the trace job copied them there);
// this pass stamps the slot's token before selection or a relight reads it. The next pass reads only after the cache
// write/read barrier.
[numthreads(32, 1, 1)]
void CSMain(uint3 group : SV_GroupID, uint lane : SV_GroupIndex) {
    uint input = group.x * SDF_PROBE_RELIGHT_INPUT_WORDS;
    uint output = group.x * SDF_PROBE_RELIGHT_RESULT_WORDS;
    for (uint word = lane; word < SDF_PROBE_RELIGHT_RESULT_WORDS; word += 32) {
        sdfProbeHitsRW[output + word] = word < SDF_PROBE_RELIGHT_OUTPUT_WORDS ? 0 :
            sdfProbeInput[input + SDF_PROBE_RECORD_INPUT_WORD + word - SDF_PROBE_RELIGHT_OUTPUT_WORDS];
    }
    if ((sdfProbeInput[input] & 4u) == 0) { return; }
    uint cache = sdfProbeInput[input + 3] * SDF_PROBE_CACHE_WORDS;
    if (lane < 2) { sdfProbeCacheRW[cache + lane] = sdfProbeInput[input + 1 + lane]; }
}
