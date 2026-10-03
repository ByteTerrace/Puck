// The gradient kind: the stops, piecewise-linear in the layer-frame direction's height and clamped to the end stops beyond
// the first and last (the validator orders them and requires two). Opaque. One evaluation a sample. CPU reference:
// SdfSkyEnvironment.Gradient.
#ifndef SKY_KINDS_GRADIENT_HLSLI
#define SKY_KINDS_GRADIENT_HLSLI

float4 sdfSkyGradientLayer(SdfSkyGradient gradient, SdfSkyLayer layer, SdfSkySample sample) {
    puckCountDetail(layer.Detail, 0u, 0u, 1u, 0u, 0u);

    float3 colors[SDF_SKY_MAX_STOPS] = { gradient.Color0, gradient.Color1, gradient.Color2, gradient.Color3 };
    float elevations[SDF_SKY_MAX_STOPS] = { gradient.Elevation0, gradient.Elevation1, gradient.Elevation2, gradient.Elevation3 };
    uint stops = min(gradient.Count, SDF_SKY_MAX_STOPS);
    float elevation = sample.local.y;
    float3 previousColor = colors[0];
    float previousElevation = elevations[0];

    if (elevation <= previousElevation) {
        return float4(previousColor, 1.0);
    }

    [loop]
    for (uint index = 1u; (index < stops); index++) {
        float3 nextColor = colors[index];
        float nextElevation = elevations[index];

        if (elevation <= nextElevation) {
            float t = saturate((elevation - previousElevation) / max((nextElevation - previousElevation), 1.0e-5));

            return float4(lerp(previousColor, nextColor, t), 1.0);
        }

        previousColor = nextColor;
        previousElevation = nextElevation;
    }

    return float4(previousColor, 1.0);
}
#endif
