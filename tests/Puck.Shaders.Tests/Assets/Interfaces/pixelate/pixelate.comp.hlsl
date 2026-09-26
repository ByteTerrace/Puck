// A pixelate/posterize pass read through its generated pass interface instead of a push constant and hand-numbered
// bindings. pixelate.interface.hlsli is generated from the interface ShaderInterfaceSpikeTests declares and written
// beside this file at test time; it is never checked in. The frame group carries the extent, the pass group the cell
// size, a per-channel level count in a four-component value, and the two storage images. Equal levels on every channel
// quantize exactly as a single level count would.
#include "pixelate.interface.hlsli"

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint2 extent = frameGroup.extent;

    if ((id.x >= extent.x) || (id.y >= extent.y)) {
        return;
    }

    uint cell = max(1u, passGroup.cellSize);
    uint2 cellCoord = min((((id.xy / cell) * cell) + (cell / 2u)), (extent - uint2(1u, 1u)));
    float3 color = saturate(source[cellCoord].rgb);

    [unroll]
    for (uint channel = 0u; (channel < 3u); channel++) {
        uint levels = passGroup.channelLevels[channel];

        if (levels > 1u) {
            float steps = ((float)levels - 1.0);

            color[channel] = (round(color[channel] * steps) / steps);
        }
    }

    output[id.xy] = float4(color, 1.0);
}
