// The binding station's compute pass over both groups: it reads its extent and config (cellSize, levels) from the pass
// group's block, its source through a formatted load, and writes its destination storage image, every binding placed
// by the generated interface. Each cell takes its centre texel, quantized to its channel's level count.
#include "binding-pixelate.interface.hlsli"

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    uint2 extent = passGroup.extent;

    if (any(id.xy >= extent)) {
        return;
    }

    uint cell = max(1u, passGroup.cellSize);
    uint2 centre = min((((id.xy / cell) * cell) + (cell / 2u)), (extent - 1u));
    float3 color = saturate(source.Load(int3(centre, 0)).rgb);
    float3 steps = (float3(max(passGroup.levels, 2u)) - 1.0);

    destination[id.xy] = float4((round(color * steps) / steps), 1.0);
}
