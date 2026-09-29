// Ordered gradient stops in the caller's local sky frame. The generated interface declares both native records.
#ifndef SKY_KIND_GRADIENT_HLSLI
#define SKY_KIND_GRADIENT_HLSLI

// This consumes the resolver's admitted order without sorting or inventing a crossing policy. Equal stops retain
// the existing first-encountered boundary rule. Empty ranges read no table record and contribute no coverage.
float4 sdfSkyGradient(float3 localDirection, SdfSkyGradientData parameters,
    StructuredBuffer<SdfSkyStopData> stops, inout uint hashes) {
    if (parameters.StopCount == 0u) return 0.0;
    float elevation = localDirection.y;
    SdfSkyStopData previous = stops[parameters.FirstStop];
    if (parameters.StopCount == 1u || elevation <= previous.Elevation) return float4(previous.Color, 1.0);
    [loop] for (uint index = 1u; index < parameters.StopCount; index++) {
        SdfSkyStopData next = stops[parameters.FirstStop + index];
        if (elevation <= next.Elevation) {
            float t = saturate((elevation - previous.Elevation) / (next.Elevation - previous.Elevation));
            return float4(lerp(previous.Color, next.Color, t), 1.0);
        }
        previous = next;
    }
    return float4(previous.Color, 1.0);
}
#endif
