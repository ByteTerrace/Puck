// Generic shading layers. Coordinates arrive in the winning instance frame.
#ifndef SDF_SHADE_LAYERS_HLSLI
#define SDF_SHADE_LAYERS_HLSLI
float sdfWrapDiffuse(float ndotl, float wrap) {
    return max(((ndotl + wrap) / (1.0 + wrap)), 0.0);
}

// A wide-stencil (SdfSoftenProbeEpsilon) tetrahedron field-gradient probe — the same 4-tap technique
// calculateNormal uses, at a much larger epsilon, so fine surface detail (pores, panel seams, wear noise) is
// per-part authored 'guide' ellipsoid normal without needing a guide shape.
static const float SdfSoftenProbeEpsilon = 0.05;

float3 sdfSoftenedNormal(float3 p, uint instanceMaskBase) {
    const float2 k = float2(1.0, -1.0);
    const float e = SdfSoftenProbeEpsilon;
    float3 sum =
        (k.xyy * mapDistanceMasked(p + (k.xyy * e), instanceMaskBase)) +
        (k.yyx * mapDistanceMasked(p + (k.yyx * e), instanceMaskBase)) +
        (k.yxy * mapDistanceMasked(p + (k.yxy * e), instanceMaskBase)) +
        (k.xxx * mapDistanceMasked(p + (k.xxx * e), instanceMaskBase));

    return sdfSafeNormalize(sum);
}

// Blends `normal` toward the wide-stencil guide by `soften`, in place. soften <= 0 skips the extra 4-tap probe
// entirely, matching applyWeathering's zero-ceiling skip.
void applySoften(inout float3 normal, float3 p, uint instanceMaskBase, float soften) {
    if (soften <= 0.0) {
        return;
    }

    normal = normalize(lerp(normal, sdfSoftenedNormal(p, instanceMaskBase), saturate(soften)));
}


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
