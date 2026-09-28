// The binding station's compute pass over both groups: it reads its extent and config (cellSize, levels) from the pass
// group's block, its source through a formatted load, and writes its destination storage image, every binding placed
// by the generated interface. Each cell takes its centre texel, quantized to its channel's level count in integer
// codes, so the stored value is a whole number of 255ths and both backends write the same bytes: no rounding of a
// half-way unorm value is left to an implementation.
#include "binding-pixelate.interface.hlsli"

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    uint2 extent = passGroup.extent;

    if (any(id.xy >= extent)) {
        return;
    }

    uint cell = max(1u, passGroup.cellSize);
    uint2 centre = min((((id.xy / cell) * cell) + (cell / 2u)), (extent - 1u));
    uint3 code = (uint3)round(saturate(source.Load(int3(centre, 0)).rgb) * 255.0);
    uint3 steps = (max(passGroup.levels, 2u) - 1u);
    uint3 level = (((code * steps) + 127u) / 255u);

    destination[id.xy] = float4((float3(((level * 255u) + (steps / 2u)) / steps) / 255.0), 1.0);
}
