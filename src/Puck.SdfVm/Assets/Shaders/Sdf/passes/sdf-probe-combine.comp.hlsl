#include "../isa/sdf-probe-relight.interface.hlsli"
#include "../isa/sdf-render.hlsli"
// KEEP IN SYNC with sdf-probe-relight's partial layout (SdfMeshProbeRelight.PartialWords) and SdfMeshIrradiance.Relight.
// One group a batch origin: sums the probe's relight slices in slice order into its lighting output (six RGB varying,
// six local, the visibility bits, six RGB sky light) and packs its publication record. An origin the relight did not list (an invalid
// probe) is left unwritten, as the host reads none of its words.
[numthreads(SDF_PROBE_GROUP_SIZE, 1, 1)]
void CSMain(uint3 group : SV_GroupID, uint lane : SV_GroupIndex) {
    uint count, stride;
    sdfProbeOrigins.GetDimensions(count, stride);
    uint probe = group.x;
    if (probe >= count) { return; }
    uint input = probe * SDF_PROBE_RELIGHT_INPUT_WORDS;
    uint flags = sdfProbeInput[input];
    if ((flags & 1u) == 0) { return; }
    bool reuse = (flags & 2u) != 0;
    uint cache = sdfProbeInput[input + 3] * SDF_PROBE_CACHE_WORDS + SDF_PROBE_CACHE_RESULT_WORD;
    bool device = (flags & 8u) != 0;
    uint output = probe * SDF_PROBE_RELIGHT_RESULT_WORDS;
    uint partials = count * SDF_PROBE_RELIGHT_RESULT_WORDS + 1 + probe * SDF_PROBE_RELIGHT_SLICES * SDF_PROBE_RELIGHT_PARTIAL_WORDS;
    if (lane < 6) {
        float3 varying = 0, local = 0, sky = 0;
        for (uint slice = 0; slice < SDF_PROBE_RELIGHT_SLICES; slice++) {
            uint at = partials + slice * SDF_PROBE_RELIGHT_PARTIAL_WORDS + 3 * lane;
            varying += asfloat(uint3(sdfProbeHitsRW[at], sdfProbeHitsRW[at + 1], sdfProbeHitsRW[at + 2]));
            local += asfloat(uint3(sdfProbeHitsRW[at + 18], sdfProbeHitsRW[at + 19], sdfProbeHitsRW[at + 20]));
            uint open = at + SDF_PROBE_RELIGHT_PARTIAL_SKY_WORD;
            sky += asfloat(uint3(sdfProbeHitsRW[open], sdfProbeHitsRW[open + 1], sdfProbeHitsRW[open + 2]));
        }
        float total = sdfProbeLighting[9 + lane / 4][lane % 4];
        uint previous = input + SDF_PROBE_RELIGHT_PREVIOUS_WORD + 18 + 3 * lane;
        float3 keptLocal = device ? asfloat(uint3(sdfProbeCache[cache + 18 + 3 * lane], sdfProbeCache[cache + 19 + 3 * lane], sdfProbeCache[cache + 20 + 3 * lane])) :
            asfloat(uint3(sdfProbeInput[previous], sdfProbeInput[previous + 1], sdfProbeInput[previous + 2]));
        local = reuse ? keptLocal : local / total;
        varying = varying / total + local;
        sky /= total;
        [unroll] for (uint c = 0; c < 3; c++) {
            sdfProbeHitsRW[output + 3 * lane + c] = asuint(varying[c]);
            sdfProbeHitsRW[output + 18 + 3 * lane + c] = asuint(local[c]);
            sdfProbeHitsRW[output + SDF_PROBE_RELIGHT_SKY_OUTPUT_WORD + 3 * lane + c] = asuint(sky[c]);
        }
    }
    uint record = output + SDF_PROBE_RELIGHT_OUTPUT_WORDS;
    for (uint w = lane; w < SDF_MESH_IRRADIANCE_LIGHT_WORDS; w += SDF_PROBE_GROUP_SIZE) {
        uint set = 0, cleared = 0;
        for (uint slice = 0; slice < SDF_PROBE_RELIGHT_SLICES; slice++) {
            uint at = partials + slice * SDF_PROBE_RELIGHT_PARTIAL_WORDS + 36 + w;
            set |= sdfProbeHitsRW[at];
            cleared |= sdfProbeHitsRW[at + SDF_MESH_IRRADIANCE_LIGHT_WORDS];
        }
        uint keptVisibility = device ? sdfProbeCache[cache + 36 + w] : sdfProbeInput[input + SDF_PROBE_RELIGHT_PREVIOUS_WORD + 36 + w];
        uint visible = ((reuse ? keptVisibility : 0) | set) & ~cleared;
        sdfProbeHitsRW[output + 36 + w] = visible;
        sdfProbeHitsRW[record + SDF_MESH_IRRADIANCE_LIGHTS_WORD + w] = visible;
    }
    DeviceMemoryBarrierWithGroupSync();
    if (lane < 9) {
        sdfProbeHitsRW[record + SDF_MESH_IRRADIANCE_RADIANCE_WORD + lane] = f32tof16(asfloat(sdfProbeHitsRW[output + 2 * lane])) |
            (f32tof16(asfloat(sdfProbeHitsRW[output + 2 * lane + 1])) << 16);
        uint open = output + SDF_PROBE_RELIGHT_SKY_OUTPUT_WORD + 2 * lane;
        sdfProbeHitsRW[record + SDF_MESH_IRRADIANCE_SKY_LIGHT_WORD + lane] = f32tof16(asfloat(sdfProbeHitsRW[open])) |
            (f32tof16(asfloat(sdfProbeHitsRW[open + 1])) << 16);
    }
    DeviceMemoryBarrierWithGroupSync();
    if (lane == 0) {
        bool moved = (flags & 4u) != 0;
        if (!moved) {
            for (uint axis = 0; axis < 18; axis++) {
                uint skyWord = SDF_PROBE_RELIGHT_SKY_OUTPUT_WORD + axis;
                float now = asfloat(sdfProbeHitsRW[output + axis]);
                float before = asfloat(sdfProbeCache[cache + axis]);
                float skyNow = asfloat(sdfProbeHitsRW[output + skyWord]);
                float skyBefore = asfloat(sdfProbeCache[cache + skyWord]);
                moved = moved || abs(now - before) > SDF_PROBE_CONVERGED_SHARE * max(abs(now), abs(before)) + SDF_PROBE_CONVERGED_FLOOR ||
                    abs(skyNow - skyBefore) > SDF_PROBE_CONVERGED_SHARE * max(abs(skyNow), abs(skyBefore)) + SDF_PROBE_CONVERGED_FLOOR;
            }
        }
        sdfProbeHitsRW[output + SDF_PROBE_RELIGHT_MOVED_WORD] = moved ? 1u : 0u;
    }
}
