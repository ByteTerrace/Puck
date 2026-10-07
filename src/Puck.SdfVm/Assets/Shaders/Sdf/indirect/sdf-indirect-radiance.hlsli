// Resource-free lighting storage. SdfIndirectRadiance owns the matching CPU reference.
#ifndef SDF_INDIRECT_RADIANCE_HLSLI
#define SDF_INDIRECT_RADIANCE_HLSLI
uint sdfIndirectPackRadiance(float3 radiance) {
    // Canonical positive zero keeps a half sign bit from spilling into the next unsigned channel.
    float3 finite = float3(radiance.x > 0.0 ? radiance.x : 0.0,
        radiance.y > 0.0 ? radiance.y : 0.0, radiance.z > 0.0 ? radiance.z : 0.0);
    uint3 halves = f32tof16(clamp(finite, 0.0, float3(65024.0, 65024.0, 64512.0)));
    uint3 shift = uint3(4u, 4u, 5u);
    uint3 packed = (halves + uint3(7u, 7u, 15u) + ((halves >> shift) & 1u)) >> shift;
    return packed.x | (packed.y << 11u) | (packed.z << 22u);
}
float3 sdfIndirectUnpackRadiance(uint packed) {
    return f16tof32(uint3((packed & 0x7ffu) << 4u, ((packed >> 11u) & 0x7ffu) << 4u, (packed >> 22u) << 5u));
}
#endif
