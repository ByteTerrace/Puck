// Film grain (single-source HLSL; DXC compiles it to SPIR-V for Vulkan and DXIL for Direct3D 12): a fullscreen
// pass over the inner render node's own output, adding a per-pixel integer-hashed grain offset. Reuses
// fullscreen.vert.hlsl (no vertex stage of its own) and sdfPcg3d (sdf-vm.hlsli) rather than a new hash — both are
// already-proven, integer-only, bit-identical across DXC's two targets.
//
// On Vulkan the texture+sampler fuse into one combined image sampler at set 0 binding 0; on Direct3D 12 they are
// t0/s0 (a static sampler baked into the root signature). The frame block is the set's generated interface
// (sdf-film-grain.interface.hlsli, regenerated with `puck shaders interface --write`): the engine tick, the tick rate
// and the set's config. The grain frame is the tick quantized to the flicker period, never a wall-clock or RNG value,
// so the same simulation moment hashes identically on every run, machine, and backend.
#include "sdf-vm.hlsli"
#include "sdf-film-grain.interface.hlsli"

[[vk::combinedImageSampler]][[vk::binding(0, 0)]] Texture2D sourceTexture : register(t0);
[[vk::combinedImageSampler]][[vk::binding(0, 0)]] SamplerState sourceSampler : register(s0);

// The low 32 bits of floor(tick / period) for the 64-bit tick (low word, high word) and a period below 2^16, divided
// sixteen bits at a time so no intermediate leaves 32 bits.
uint quantizeTick(uint2 tick, uint period) {
    uint remainder = (tick.y % period);
    uint upper = ((remainder << 16) | (tick.x >> 16));
    uint quotientUpper = (upper / period);
    uint lower = (((upper % period) << 16) | (tick.x & 0xFFFFu));

    return ((quotientUpper << 16) + (lower / period));
}

float4 PSMain(float4 fragCoord : SV_Position) : SV_Target {
    uint width;
    uint height;

    sourceTexture.GetDimensions(width, height);

    float2 uv = (fragCoord.xy / float2(width, height));
    float3 sourceColor = sourceTexture.Sample(sourceSampler, uv).rgb;

    // Both backends floor the same half-pixel-centre SV_Position identically, so the cell index — and the hash it
    // keys — is bit-identical cross-backend. size >= 1 pixel; a caller-supplied size below that would divide by a
    // sub-pixel cell and is clamped here rather than trusted.
    float cellSize = max(frameGroup.size, 1.0);
    uint2 cell = uint2(floor(fragCoord.xy / cellSize));
    uint period = max((frameGroup.tickRate / max(frameGroup.flickerHz, 1u)), 1u);
    uint grainFrame = quantizeTick(frameGroup.tick, period);
    uint3 hash = sdfPcg3d(uint3(cell.x, cell.y, (grainFrame ^ frameGroup.seed)));
    float noise = ((float(hash.x) / 4294967295.0) * 2.0 - 1.0);

    return float4(saturate(sourceColor + (noise * frameGroup.intensity)), 1.0);
}
