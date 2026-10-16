// Generic shading layers. Coordinates arrive in the winning instance frame.
#ifndef SDF_SHADE_LAYERS_HLSLI
#define SDF_SHADE_LAYERS_HLSLI
// Refract into the authored local plane, then sample its radial ramp. No view-facing frame is synthesized.
void applyInset(float3 position, float3 normal, float3 ray, inout SdfMaterialData material) {
    uint count = (uint)material.paintControls.y;
    if (count == 0u) return;
    float4 rotation = material.insetRotation;
    float3 p = rotatePointByInverseQuaternion(position - material.insetOriginDepth.xyz, rotation);
    float3 n = rotatePointByInverseQuaternion(normal, rotation);
    float3 d = rotatePointByInverseQuaternion(ray, rotation);
    if (dot(n, d) > 0.0) n = -n;
    d = refract(d, n, 1.0 / material.paintControls.x);
    if (dot(d, d) < 1.0e-12 || abs(d.z) < 1.0e-6) return;
    float t = (-material.insetOriginDepth.w - p.z) / d.z;
    if (t < 0.0) return;
    float2 uv = (p + t * d).xy;
    float radius = length(uv);
    float phase = (float)sdfPcg3d(uint3(asuint(material.paintModulation.y), 0u, 0u)).x * SDF_INV_2POW32 * 2.0 * SDF_PI;
    radius *= 1.0 + material.paintControls.w * sin(atan2(uv.y, uv.x) * material.paintModulation.x + phase);
    float3 color = material.paintStops[0].rgb;
    [loop] for (uint i = 1u; i < count; i++) {
        float4 left = material.paintStops[i - 1u];
        float4 right = material.paintStops[i];
        float width = max((right.w - left.w) * material.paintControls.z, 1.0e-6);
        float blend = smoothstep(right.w - width, right.w, radius);
        color = lerp(color, right.rgb, blend);
    }
    material.albedo = color;
}
#endif
