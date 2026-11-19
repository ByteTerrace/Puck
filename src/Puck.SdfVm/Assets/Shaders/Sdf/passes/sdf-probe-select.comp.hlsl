#include "../isa/sdf-probe-relight.interface.hlsli"
#include "../isa/sdf-render.hlsli"
// KEEP IN SYNC with SdfMeshIrradiance.Affected, the CPU reference List and the rows SdfMeshIrradiance.WriteChanges
// packs. The GPU owns eligibility; the host retains the priority sort while it coordinates relocation and the
// diagnostic cache.
bool probeMeets(float3 origin, float3 direction, float3 low, float3 high, float reach) {
    float3 inverse = 1 / direction;
    float3 a = (low - origin) * inverse, b = (high - origin) * inverse;
    float3 near = min(a, b), far = max(a, b);
    return max(max(near.x, near.y), max(near.z, 0)) <= min(min(far.x, far.y), min(far.z, reach));
}
// A recorded hit's reach with the margins a box test grants it.
float probeExtended(float reach) {
    return reach + max(SDF_MESH_IRRADIANCE_BOX_MARGIN, reach * SDF_MESH_IRRADIANCE_BOX_MARGIN_SHARE) + SDF_MESH_IRRADIANCE_DEPTH_SHEET_THICKNESS;
}
// The distance every ray of a probe ends within: a box farther from its origin meets none of them.
float probeRayReach(uint hits, float limit) {
    float far = 0;
    for (uint ray = 0; ray < SDF_PROBE_HIT_COUNT; ray++) { far = max(far, min(abs(asfloat(sdfProbeCache[hits + 2 * ray])), limit)); }
    return probeExtended(far);
}
bool probeNear(float3 origin, float3 low, float3 high, float radius) {
    float3 outside = max(max(low - origin, origin - high), 0);
    return dot(outside, outside) <= radius * radius;
}
// Whether a box meets one of the probe's rays before the hit it recorded.
bool probeRaysMeet(float3 origin, float3 low, float3 high, uint hits, float limit) {
    float3 toward = (low + high) * 0.5 - origin;
    float radius = length((high - low) * 0.5);
    float distance = length(toward);
    if (distance <= radius) { return true; }
    float3 axis = toward / distance;
    float cone = sqrt(1 - radius * radius / (distance * distance));
    for (uint ray = 0; ray < SDF_PROBE_HIT_COUNT; ray++) {
        float3 direction = sdfProbeDirections[ray].xyz;
        if (dot(direction, axis) < cone) { continue; }
        float reach = min(abs(asfloat(sdfProbeCache[hits + 2 * ray])), limit);
        if (probeMeets(origin, direction, low, high, probeExtended(reach))) { return true; }
    }
    return false;
}
[numthreads(SDF_PROBE_SELECT_GROUP_SIZE, 1, 1)]
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
        float reach = -1;
        // Each change newer than the probe's trace: one of every probe traces it; any other is a hierarchy walked in
        // depth-first order, a subtree no placement or ray of the probe can reach skipped to its escape.
        for (uint change = 0; !trace && change < (uint)limits.x; change++) {
            float4 head = sdfProbeLighting[SDF_PROBE_SELECT_HEADER_ROWS + change];
            if (asuint(head.x) <= geometry) { continue; }
            if ((asuint(head.w) & 1u) != 0) { trace = true; break; }
            uint first = asuint(head.y);
            uint nodes = asuint(head.z);
            if (nodes != 0 && reach < 0) { reach = probeRayReach(hits, limits.w); }
            for (uint node = 0; !trace && node < nodes;) {
                float4 low = sdfProbeLighting[first + 2 * node];
                float4 high = sdfProbeLighting[first + 2 * node + 1];
                bool placed = all(max(low.xyz, center - placement) <= min(high.xyz, center + placement));
                if (!placed && !probeNear(origin, low.xyz, high.xyz, reach)) { node = asuint(low.w); continue; }
                if (high.w == 0) { node++; continue; }
                trace = placed || probeRaysMeet(origin, low.xyz, high.xyz, hits, limits.w);
                node = asuint(low.w);
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
    // A probe relit in this sweep waits for the next one unless the lighting it was relit under has been replaced.
    bool behind = lighting != asuint(state.z) || sweep != asuint(state.w);
    bool due = essential || forced || (behind && (cadence.x == 0 || phaseDue));
    bool stale = !due && behind;
    sdfProbeHitsRW[id.x] = (trace ? 1u : 0u) | (due ? 2u : 0u) | (stale ? 4u : 0u);
}
