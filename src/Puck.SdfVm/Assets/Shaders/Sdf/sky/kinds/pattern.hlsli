// An analytic checker in the caller's transformed sky frame. It performs no hash work.
#ifndef SKY_KIND_PATTERN_HLSLI
#define SKY_KIND_PATTERN_HLSLI
#include "../../field/sdf-octahedral.hlsli"

float4 sdfSkyPattern(float3 localDirection, SdfSkyPatternData parameters, uint quality, inout uint hashes) {
    if (parameters.Intensity <= 0.0) return 0.0;
    float2 uv = sdfOctEncode(localDirection) * 0.5 + 0.5;
    float2 cell = floor(uv * parameters.Cells + parameters.Offset);
    float2 parity = cell - 2.0 * floor(cell * 0.5);
    bool odd = parity.x != parity.y;
    return float4((odd ? parameters.ColorB : parameters.ColorA) * parameters.Intensity, 1.0);
}
#endif
