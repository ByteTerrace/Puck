// The film-grain pass (src/Puck.SdfVm/Assets/Shaders/Sdf/sdf-film-grain.frag.hlsl) read through its generated pass
// interface instead of a push constant and hand-numbered bindings. film-grain.interface.hlsli is generated from the
// interface ShaderInterfaceSpikeTests declares and written beside this file at test time; it is never checked in.
// The frame group carries the deterministic tick and the extent, the pass group the parameters, the source image and
// its sampler. The grain frame is the tick divided by the flicker period in ticks, and tint scales the grain per
// channel, so a white tint and a period matching the shipped pass's quantization grain identically.
#include "sdf-vm.hlsli"
#include "film-grain.interface.hlsli"

float4 PSMain(float4 fragCoord : SV_Position) : SV_Target {
    float2 uv = (fragCoord.xy / float2(frameGroup.extent));
    float3 sourceColor = source.Sample(sourceSampler, uv).rgb;
    float cellSize = max(passGroup.cellSize, 1.0);
    uint2 cell = uint2(floor(fragCoord.xy / cellSize));
    uint grainFrame = (frameGroup.tick / max(passGroup.flickerTicks, 1u));
    uint3 hash = sdfPcg3d(uint3(cell.x, cell.y, (grainFrame ^ passGroup.seed)));
    float noise = ((float(hash.x) / 4294967295.0) * 2.0 - 1.0);

    return float4(saturate(sourceColor + (noise * passGroup.intensity * passGroup.tint)), 1.0);
}
