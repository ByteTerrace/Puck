// The K row's four independent eight-bit visibilities. The CPU pair is SdfVisibility.PackShadows/ShadowAt.
#ifndef FRAME_SDF_SHADOW_VISIBILITY_HLSLI
#define FRAME_SDF_SHADOW_VISIBILITY_HLSLI
#include "../isa/sdf-isa.hlsli"

uint sdfPackShadowVisibility(float4 visibility) {
    uint4 code = (uint4)floor((saturate(visibility) * float(SDF_SHADOW_MASK)) + 0.5);
    return (code.x | (code.y << SDF_SHADOW_BITS) | (code.z << (2u * SDF_SHADOW_BITS)) | (code.w << (3u * SDF_SHADOW_BITS)));
}
float4 sdfUnpackShadowVisibility(uint word) {
    uint4 code = ((uint4(word, word, word, word) >> (uint4(0u, 1u, 2u, 3u) * SDF_SHADOW_BITS)) & SDF_SHADOW_MASK);
    return (float4(code) / float(SDF_SHADOW_MASK));
}

#endif
