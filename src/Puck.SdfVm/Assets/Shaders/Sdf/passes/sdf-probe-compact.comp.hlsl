#include "../isa/sdf-probe-relight.interface.hlsli"
#include "../isa/sdf-render.hlsli"
// One bounded batch, in its prior priority order. No atomics reorder bounce dependencies across devices. The relight
// dispatch runs SDF_PROBE_RELIGHT_SLICES groups for each listed probe.
[numthreads(1, 1, 1)]
void CSMain() {
    uint count, stride;
    sdfProbeOrigins.GetDimensions(count, stride);
    uint selected = 0;
    for (uint i = 0; i < count; i++) {
        if ((sdfProbeInput[i * SDF_PROBE_RELIGHT_INPUT_WORDS] & 1u) != 0) { sdfProbeWorkRW[3 + selected++] = i; }
    }
    sdfProbeWorkRW[0] = selected * SDF_PROBE_RELIGHT_SLICES;
    sdfProbeWorkRW[1] = 1;
    sdfProbeWorkRW[2] = 1;
}
