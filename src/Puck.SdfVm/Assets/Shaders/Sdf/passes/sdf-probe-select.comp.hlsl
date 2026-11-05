#include "../isa/sdf-probe-relight.interface.hlsli"
#include "../isa/sdf-render.hlsli"
// KEEP IN SYNC with SdfMeshIrradiance.Affected and the CPU reference List. The GPU owns eligibility; the host
// retains the priority sort while it coordinates relocation and the diagnostic cache.
bool probeMeets(float3 origin, float3 direction, float3 low, float3 high, float reach) {
    float3 inverse = 1 / direction;
    float3 a = (low - origin) * inverse, b = (high - origin) * inverse;
    float3 near = min(a, b), far = max(a, b);
    return max(max(near.x, near.y), max(near.z, 0)) <= min(min(far.x, far.y), min(far.z, reach));
}
[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint count, stride;
    sdfProbeOrigins.GetDimensions(count, stride);
    if (id.x >= count) { return; }
    uint at = id.x * SDF_PROBE_SELECT_INPUT_WORDS;
    bool present = (sdfProbeInput[at] & 1u) != 0;
    uint cache = sdfProbeInput[at + 8] * SDF_PROBE_CACHE_WORDS;
    uint2 token = uint2(sdfProbeInput[at + 9], sdfProbeInput[at + 10]);
    present = present && any(token != 0) && all(token == uint2(sdfProbeCache[cache], sdfProbeCache[cache + 1]));
    uint geometry = sdfProbeInput[at + 1];
    uint lighting = sdfProbeInput[at + 2];
    uint sweep = sdfProbeInput[at + 3];
    uint level = sdfProbeInput[at + 4];
    float4 state = sdfProbeLighting[0];
    float4 limits = sdfProbeLighting[1];
    bool trace = !present;
    if (present && geometry != asuint(state.y)) {
        trace = limits.x == 0 || geometry + 1 < asuint(limits.z);
        float3 spacing = sdfProbeLighting[2 + level].xyz;
        float3 center = sdfProbeOrigins[id.x].xyz;
        float3 origin = center + asfloat(uint3(sdfProbeInput[at + 5], sdfProbeInput[at + 6], sdfProbeInput[at + 7]));
        float3 placement = 1.5 * spacing + SDF_MESH_IRRADIANCE_CLEARANCE;
        uint hits = cache + 2;
        for (uint box = 0; !trace && box < (uint)limits.y; box++) {
            float4 low = sdfProbeLighting[SDF_PROBE_SELECT_HEADER_ROWS + 2 * box];
            float4 high = sdfProbeLighting[SDF_PROBE_SELECT_HEADER_ROWS + 1 + 2 * box];
            if (asuint(low.w) <= geometry) { continue; }
            if (high.w != 0 || all(max(low.xyz, center - placement) <= min(high.xyz, center + placement))) { trace = true; break; }
            float3 toward = (low.xyz + high.xyz) * 0.5 - origin;
            float radius = length((high.xyz - low.xyz) * 0.5);
            float distance = length(toward);
            if (distance <= radius) { trace = true; break; }
            float3 axis = toward / distance;
            float cone = sqrt(1 - radius * radius / (distance * distance));
            for (uint ray = 0; !trace && ray < SDF_PROBE_HIT_COUNT; ray++) {
                float3 direction = sdfProbeDirections[ray].xyz;
                if (dot(direction, axis) < cone) { continue; }
                float reach = min(abs(asfloat(sdfProbeCache[hits + 2 * ray])), limits.w);
                trace = probeMeets(origin, direction, low.xyz, high.xyz, reach + max(0.001, reach * 0.0001) + SDF_MESH_IRRADIANCE_DEPTH_SHEET_THICKNESS);
            }
        }
    }
    bool rebase = present && !trace && geometry != asuint(state.y);
    bool essential = state.x != 0 || (sdfProbeInput[at] & 2u) != 0 || trace || rebase;
    float4 cadence = sdfProbeLighting[5 + level];
    float age = asfloat(sdfProbeInput[at + 11]);
    uint phase = sdfProbeInput[at] >> 2;
    bool phaseDue = (asuint(cadence.w) + phase) % (uint)cadence.y == 0;
    bool forced = cadence.z > 0 && age >= cadence.z;
    bool due = essential || forced || (sweep != asuint(state.w) && (cadence.x == 0 || phaseDue));
    bool stale = !due && (lighting != asuint(state.z) || sweep != asuint(state.w));
    sdfProbeHitsRW[id.x] = (trace ? 1u : 0u) | (due ? 2u : 0u) | (stale ? 4u : 0u);
}
