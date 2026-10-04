// A rectangle in the tangent frame about Direction. Roughness widens both edges and conserves their integrated weight.
#ifndef SHADE_SDF_SKY_PANEL_HLSLI
#define SHADE_SDF_SKY_PANEL_HLSLI
float4 sdfSkyPanelValue(SdfSkyPanel panel, float3 direction, float roughness) {
    if (panel.Intensity <= 0.0) {
        return 0.0.xxxx;
    }
    float facing = dot(direction, panel.Direction);
    if (facing <= 0.0) {
        return 0.0.xxxx;
    }
    float3 axis = ((abs(panel.Direction.y) < 0.999) ? float3(0.0, 1.0, 0.0) : float3(0.0, 0.0, 1.0));
    float3 right = normalize(cross(axis, panel.Direction));
    float3 up = cross(panel.Direction, right);
    float2 angle = abs(float2(atan2(dot(direction, right), facing), atan2(dot(direction, up), facing)));
    float width = max(panel.Blur, roughness);
    float2 extent = panel.Size + roughness;
    float2 edge = float2(1.0 - sdfSkyRise(extent.x + width, width, angle.x), 1.0 - sdfSkyRise(extent.y + width, width, angle.y));
    float gain = (panel.Size.x * panel.Size.y) / max(extent.x * extent.y, 1.0e-12);
    return float4(panel.Color * panel.Intensity * gain, edge.x * edge.y);
}
#endif
