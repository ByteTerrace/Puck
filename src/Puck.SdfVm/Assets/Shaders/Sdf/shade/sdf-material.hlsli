// The material table decoder; resource-free shading math lives in sdf-material-response.hlsli.
#ifndef SHADE_SDF_MATERIAL_HLSLI
#define SHADE_SDF_MATERIAL_HLSLI
#include "sdf-material-response.hlsli"

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
    data.bounce = asfloat(sdfWords[materialBase + 3u]).rgb;
    data.insetOriginDepth = asfloat(sdfWords[materialBase + 4u]);
    data.insetRotation = asfloat(sdfWords[materialBase + 5u]);
    data.paintControls = asfloat(sdfWords[materialBase + 6u]);
    data.paintModulation = asfloat(sdfWords[materialBase + 7u]);
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
#endif
