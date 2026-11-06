// Calls the production glass accumulation and decoding (frame/sdf-glass.hlsli). Each workgroup adds every face of one
// case to its own pixel's sums, one face after another in the order the law supplies, so the law compares orders
// deterministically, then decodes the pixel and writes its raw words beside the decoded values.
[[vk::binding(0, 3)]] StructuredBuffer<float4> glassFaces : register(t0, space3);
[[vk::binding(1, 3)]] StructuredBuffer<float4> glassAbsorptions : register(t1, space3);
[[vk::binding(2, 3)]] StructuredBuffer<uint> glassOrders : register(t2, space3);
[[vk::binding(3, 3)]] RWStructuredBuffer<uint> sdfGlassSumsRW : register(u3, space3);
[[vk::binding(4, 3)]] [[vk::image_format("rgba32f")]] RWTexture2D<float4> glassResults : register(u4, space3);
#define SDF_GLASS_ACCUMULATES
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/frame/sdf-glass.hlsli"

// Each face: its ray distance, the opaque hit's distance, one for a front face or zero for a back face, and the index
// of its absorption; a face's draw is its index into the faces and its triangle three times that plus one. Each order:
// the faces of one workgroup, as indices into the faces. The first texel of a column
// holds the decoded optical depth and thickness (the thickness measured from the order's first face); the rest hold
// the pixel's raw words as 16-bit halves, which a float holds exactly.
[numthreads(1, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint faces;
    uint stride;
    glassFaces.GetDimensions(faces, stride);
    uint word = (SDF_GLASS_SUM_WORDS * id.x);

    for (uint index = 0u; (index < faces); index++) {
        uint faceIndex = glassOrders[((id.x * faces) + index)];
        float4 face = glassFaces[faceIndex];

        sdfGlassAccumulate(word, face.x, face.y, (face.z > 0.5), glassAbsorptions[(uint)face.w].xyz, faceIndex, ((3u * faceIndex) + 1u));
    }
    float4 first = glassFaces[glassOrders[(id.x * faces)]];

    glassResults[uint2(id.x, 0u)] = float4(sdfGlassOpticalDepthAt(word), sdfGlassThicknessAt(word, first.x, first.y));
    for (uint texel = 0u; (texel < ((SDF_GLASS_SUM_WORDS + 1u) / 2u)); texel++) {
        uint low = sdfGlassSumsRW[(word + (2u * texel))];
        uint high = (((2u * texel) + 1u) < SDF_GLASS_SUM_WORDS) ? sdfGlassSumsRW[(word + (2u * texel) + 1u)] : 0u;

        glassResults[uint2(id.x, (1u + texel))] = float4(low & 0xFFFFu, low >> 16u, high & 0xFFFFu, high >> 16u);
    }
}
