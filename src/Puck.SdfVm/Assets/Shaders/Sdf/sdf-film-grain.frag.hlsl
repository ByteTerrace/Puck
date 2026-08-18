// Film grain (single-source HLSL; DXC compiles it to SPIR-V for Vulkan and DXIL for Direct3D 12): a fullscreen
// pass over the inner render node's own output, adding a per-pixel integer-hashed grain offset. Reuses
// fullscreen.vert.hlsl (no vertex stage of its own) and sdfPcg3d (sdf-vm.hlsli) rather than a new hash — both are
// already-proven, integer-only, bit-identical across DXC's two targets.
//
// On Vulkan the texture+sampler fuse into one combined image sampler at set 0 binding 0; on Direct3D 12 they are
// t0/s0 (a static sampler baked into the root signature). The push constant is 16 bytes, all-integer-safe: two
// floats (intensity, size) and two uints (grainFrame, seed) — never a wall-clock or RNG value; grainFrame is the
// caller's ElapsedTicks (the deterministic fixed-step simulation clock) quantized to the authored flicker period,
// so the same fenced simulation moment hashes identically on every run, machine, and backend.
#include "sdf-vm.hlsli"

[[vk::combinedImageSampler]][[vk::binding(0, 0)]] Texture2D sourceTexture : register(t0);
[[vk::combinedImageSampler]][[vk::binding(0, 0)]] SamplerState sourceSampler : register(s0);

struct FilmGrainPushData {
    float intensity;
    float size;
    uint grainFrame;
    uint seed;
};
[[vk::push_constant]] ConstantBuffer<FilmGrainPushData> pc;

float4 PSMain(float4 fragCoord : SV_Position) : SV_Target {
    uint width;
    uint height;

    sourceTexture.GetDimensions(width, height);

    float2 uv = (fragCoord.xy / float2(width, height));
    float3 sourceColor = sourceTexture.Sample(sourceSampler, uv).rgb;

    // Both backends floor the same half-pixel-centre SV_Position identically, so the cell index — and the hash it
    // keys — is bit-identical cross-backend. size >= 1 pixel; a caller-supplied size below that would divide by a
    // sub-pixel cell and is clamped here rather than trusted.
    float cellSize = max(pc.size, 1.0);
    uint2 cell = uint2(floor(fragCoord.xy / cellSize));
    uint3 hash = sdfPcg3d(uint3(cell.x, cell.y, (pc.grainFrame ^ pc.seed)));
    float noise = ((float(hash.x) / 4294967295.0) * 2.0 - 1.0);

    return float4(saturate(sourceColor + (noise * pc.intensity)), 1.0);
}
