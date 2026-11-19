// The K row's four independent eight-bit lanes: shadow role r's cascade visibility in lane r and its cascade coverage in
// lane r + SDF_SHADOW_ROLES (sdfMeshShadow). A queried lane holds its value as a code of zero through SDF_SHADOW_SCALE; a
// lane the surface pass did not query holds SDF_SHADOW_UNQUERIED, which decodes as one and which the views filter leaves
// out of its averages (sdfViewsFilter), so a role's coverage averages over the same pixels as its visibility. No CPU code
// reads the word; SdfVisibility.ShadowBits and ShadowMask generate its lane constants.
#ifndef FRAME_SDF_SHADOW_VISIBILITY_HLSLI
#define FRAME_SDF_SHADOW_VISIBILITY_HLSLI
#include "../isa/sdf-render.hlsli"

#if ((2 * SDF_SHADOW_ROLES) > 4)
#error The K row holds a visibility and a coverage lane for each shadow role in its four lanes.
#endif

#define SDF_SHADOW_UNQUERIED SDF_SHADOW_MASK
#define SDF_SHADOW_SCALE (SDF_SHADOW_MASK - 1u)

// Packs four visibilities, a negative one as a slot not queried.
uint sdfPackShadowVisibility(float4 visibility) {
    uint4 measured = (uint4)floor((saturate(visibility) * float(SDF_SHADOW_SCALE)) + 0.5);
    uint4 code = uint4(
        ((visibility.x < 0.0) ? SDF_SHADOW_UNQUERIED : measured.x),
        ((visibility.y < 0.0) ? SDF_SHADOW_UNQUERIED : measured.y),
        ((visibility.z < 0.0) ? SDF_SHADOW_UNQUERIED : measured.z),
        ((visibility.w < 0.0) ? SDF_SHADOW_UNQUERIED : measured.w));
    return (code.x | (code.y << SDF_SHADOW_BITS) | (code.z << (2u * SDF_SHADOW_BITS)) | (code.w << (3u * SDF_SHADOW_BITS)));
}
uint4 sdfShadowCodes(uint word) {
    return ((uint4(word, word, word, word) >> (uint4(0u, 1u, 2u, 3u) * SDF_SHADOW_BITS)) & SDF_SHADOW_MASK);
}
float4 sdfUnpackShadowVisibility(uint word) {
    uint4 code = sdfShadowCodes(word);
    return float4(
        ((code.x == SDF_SHADOW_UNQUERIED) ? 1.0 : (float(code.x) / float(SDF_SHADOW_SCALE))),
        ((code.y == SDF_SHADOW_UNQUERIED) ? 1.0 : (float(code.y) / float(SDF_SHADOW_SCALE))),
        ((code.z == SDF_SHADOW_UNQUERIED) ? 1.0 : (float(code.z) / float(SDF_SHADOW_SCALE))),
        ((code.w == SDF_SHADOW_UNQUERIED) ? 1.0 : (float(code.w) / float(SDF_SHADOW_SCALE))));
}
// One for each slot the surface pass queried, zero for each it did not.
float4 sdfShadowQueried(uint word) {
    uint4 code = sdfShadowCodes(word);
    return float4(
        ((code.x == SDF_SHADOW_UNQUERIED) ? 0.0 : 1.0),
        ((code.y == SDF_SHADOW_UNQUERIED) ? 0.0 : 1.0),
        ((code.z == SDF_SHADOW_UNQUERIED) ? 0.0 : 1.0),
        ((code.w == SDF_SHADOW_UNQUERIED) ? 0.0 : 1.0));
}

#endif
