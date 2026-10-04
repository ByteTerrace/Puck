// The material table and the BRDF every shading path reads.
#ifndef SHADE_SDF_MATERIAL_HLSLI
#define SHADE_SDF_MATERIAL_HLSLI
float sdfWrapDiffuse(float ndotl, float wrap) {
    return max(((ndotl + wrap) / (1.0 + wrap)), 0.0);
}
// Generic material rows, paired with SdfProgram.Materials.cs.
struct SdfMaterialData {
    float3 albedo;
    float emissive;
    float specular;
    float roughness;
    float sheen;
    float metal;
    float coat;
    float wrap;
    float soften;
    float3 fill;
    float3 bleed;
    float receive;
    float4 insetOriginDepth;
    float4 insetRotation;
    float4 paintControls; // ior, stop count, softness, modulation amplitude
    float4 paintModulation; // frequency, seed bits, bleed green/blue
    float4 paintStops[4]; // color, radius
    float4 weathering; // edge, lines, settle, reach
    float4 weatheringControls; // seed bits, scale, floor, lane
    float4 underColorThreshold[2];
    float4 underResponse[2]; // roughness, metal, stage count, reserved
    float4 deposit;
    float4 depositResponse;
};

// The GGX roughness floor: alpha2 = roughness^2 + SdfRoughnessFloorSquared, so a bare light direction (no authored
// angular size) never collapses the GGX lobe to a delta function. SdfMaterial.DefaultRoughness is calibrated
// against this exact constant — KEEP THEM IN SYNC.
static const float SdfRoughnessFloorSquared = 0.018;
// The clearcoat lobe's fixed roughness and its fresnel-independent peak-reflectance scale (sdfMaterialShade's coat
// term). KEEP IN SYNC with SdfMaterial.Coat's XML docs.
static const float SdfCoatRoughness = 0.25;
static const float SdfCoatScale = 0.04;
// The per-material fresnel edge-lift exponent sdfMaterialShade's sheen term raises (1 - N.V) to: a broad, soft catch
// rather than a tight silhouette-only rim.
static const float SdfSheenFresnelExponent = 2.0;

// The ONE material decode point (KEEP IN SYNC with SdfProgram.Materials.cs and SdfProgram.cs).
SdfMaterialData sdfMaterialLoad(int material) {
    uint4 header = sdfWords[0];
    SdfMaterialData data = (SdfMaterialData)0;
    if ((material < 0) || ((uint)material >= SDF_PROGRAM_MATERIAL_COUNT(header))) {
        data.albedo = float3(1.0, 0.0, 1.0);
        data.emissive = 4.0;
        data.roughness = 1.0;
        return data;
    }
    uint materialBase = SDF_PROGRAM_MATERIAL_OFFSET(header) + SDF_MATERIAL_VECTORS_PER_ENTRY * (uint)material;
    float4 m0 = asfloat(sdfWords[materialBase]);
    float4 m1 = asfloat(sdfWords[materialBase + 1u]);
    float4 m2 = asfloat(sdfWords[materialBase + 2u]);
    data.albedo = m0.rgb;
    data.emissive = m0.w;
    data.specular = m1.x;
    data.roughness = m1.y;
    data.sheen = m1.z;
    data.metal = m1.w;
    data.coat = m2.x;
    data.wrap = m2.y;
    data.soften = m2.z;
    data.fill = asfloat(sdfWords[materialBase + 3u]).rgb;
    data.receive = asfloat(sdfWords[materialBase + 3u]).w;
    data.insetOriginDepth = asfloat(sdfWords[materialBase + 4u]);
    data.insetRotation = asfloat(sdfWords[materialBase + 5u]);
    data.paintControls = asfloat(sdfWords[materialBase + 6u]);
    data.paintModulation = asfloat(sdfWords[materialBase + 7u]);
    data.bleed = float3(m2.w, data.paintModulation.zw);
    [unroll] for (uint i = 0u; i < 4u; i++) data.paintStops[i] = asfloat(sdfWords[materialBase + 8u + i]);
    data.weathering = asfloat(sdfWords[materialBase + 12u]);
    data.weatheringControls = asfloat(sdfWords[materialBase + 13u]);
    [unroll] for (uint j = 0u; j < 2u; j++) {
        data.underColorThreshold[j] = asfloat(sdfWords[materialBase + 14u + 2u * j]);
        data.underResponse[j] = asfloat(sdfWords[materialBase + 15u + 2u * j]);
    }
    data.deposit = asfloat(sdfWords[materialBase + 18u]);
    data.depositResponse = asfloat(sdfWords[materialBase + 19u]);

    return data;
}
// Thin wrapper for callers that only shade the base color (debug views, palette ramps).
float3 sdfMaterialAlbedo(int material) {
    return sdfMaterialLoad(material).albedo;
}
// The Trowbridge-Reitz (GGX) normal distribution: alpha2 / (pi * d^2), d = nh^2*(alpha2-1)+1.
float sdfGgxDistribution(float nDotH, float alpha2) {
    float d = ((nDotH * nDotH) * (alpha2 - 1.0)) + 1.0;

    return (alpha2 / max((SDF_PI * d * d), 1.0e-6));
}
// The GGX specular lobe (Smith-Schlick geometry, Schlick fresnel, a roughness floor so a bare light direction stays
// finite) plus an optional fixed-roughness clearcoat lobe, for ONE light direction — factored out of sdfMaterialShade
// so a second light (an attached accent point light) can add its own specular response without re-deriving the BRDF.
// Zero when the material carries no specular/metal response, or when N.V or N.L is non-positive. `viewDirection` is
// the caller's own -rayDirection (passed rather than re-negated, matching sdfMaterialShade's original inline form
// exactly); `lightScale` scales the whole lobe by the caller's shadow/light attenuation.
float3 sdfMaterialSpecular(SdfMaterialData material, float3 normal, float3 viewDirection, float3 lightDirection, float lightScale) {
    float3 result = float3(0.0, 0.0, 0.0);

    if ((material.specular > 0.0) || (material.metal > 0.0)) {
        float3 f0 = lerp(float3(material.specular, material.specular, material.specular), material.albedo, material.metal);
        float3 halfVector = normalize(lightDirection + viewDirection);
        float nDotH = saturate(dot(normal, halfVector));
        float nDotV = saturate(dot(normal, viewDirection));
        float nDotL = saturate(dot(normal, lightDirection));

        if ((nDotV > 0.0) && (nDotL > 0.0)) {
            float rough = sqrt(((material.roughness * material.roughness) + SdfRoughnessFloorSquared));
            float alpha2 = (rough * rough);
            float k = (((material.roughness + 1.0) * (material.roughness + 1.0)) / 8.0);
            float geometry = ((nDotV / ((nDotV * (1.0 - k)) + k)) * (nDotL / ((nDotL * (1.0 - k)) + k)));
            float vDotH = saturate(dot(viewDirection, halfVector));
            float3 fresnel = (f0 + ((1.0 - f0) * pow((1.0 - vDotH), 5.0)));
            float3 specular = (((sdfGgxDistribution(nDotH, alpha2) * geometry) * fresnel) / max((4.0 * nDotV * nDotL), 1.0e-4));

            result += ((specular * nDotL) * lightScale);

            if (material.coat > 0.0) {
                float coatAlpha2 = (SdfCoatRoughness * SdfCoatRoughness);
                float coat = ((sdfGgxDistribution(nDotH, coatAlpha2) * geometry) / max((4.0 * nDotV), 1.0e-4));

                result += ((coat * (material.coat * SdfCoatScale)) * lightScale);
            }
        }
    }

    return result;
}
// The ONE lit-surface shade funnel: a metal-scaled lambert term, sdfMaterialSpecular's GGX + clearcoat lobe for the
// caller's own light direction, an emissive lift, and a fresnel sheen edge-lift. `diffuse` is the caller's
// accumulated radiance (ambient + the sun + any colored screen/point lights — a float3 so colored lights tint the
// surface); `lightScale` scales the GGX/coat lobes by the caller's shadow/light attenuation. KEEP IN SYNC across
// every caller (shade/sdf-light-stage.hlsli).
float3 sdfMaterialShade(SdfMaterialData material, float3 diffuse, float3 normal, float3 rayDirection, float3 lightDirection, float lightScale) {
    float3 diffuseAlbedo = (material.albedo * (1.0 - material.metal));
    float3 color = (diffuseAlbedo * diffuse);

    color += sdfMaterialSpecular(material, normal, -rayDirection, lightDirection, lightScale);

    if (material.emissive > 0.0) {
        color += (material.albedo * material.emissive);
    }

    if (material.sheen > 0.0) {
        float fresnel = pow(saturate(1.0 - saturate(dot(normal, -rayDirection))), SdfSheenFresnelExponent);
        color += (color * (material.sheen * fresnel));
    }

    return color;
}

#endif
