// The material table and the BRDF every shading path reads.
#ifndef SHADE_SDF_MATERIAL_HLSLI
#define SHADE_SDF_MATERIAL_HLSLI
#include "../frame/sdf-lanes.hlsli"
float sdfWrapDiffuse(float ndotl, float wrap) {
    return max(((ndotl + wrap) / (1.0 + wrap)), 0.0);
}
// Generic material rows, paired with SdfAppearance.Pack.
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
    float4 underResponse[2]; // roughness, metal, octave ratio (stage 0) or reveal softness (stage 1), pattern X/Y
    float3 deposit;
    float4 depositResponse; // roughness, metal, pattern Z, noise coverage
    float2 weatheringOctave; // second octave's frequency ratio and gain; a zero gain reads a single octave
    float4 weatheringTurn; // the pattern lattice's turn, a unit quaternion (x, y, z, w)
    uint paintFirst; // the vector of the material's first paint (SdfAppearance.PackPaint)
    uint paintCount; // its paints, drawn in order
    uint emissiveLane; // the render lane scaling emissive, plus one; 0 keeps the static emissive
    float3 absorption; // a glass's absorption per world unit (SdfGlass.Absorption); exp(-absorption * distance) transmits
    float ior; // a glass's index of refraction; zero for an opaque material
    // A glass's medium (row 31): its scatter colour and scattering per world unit (SdfGlass.Scatter, Scattering); and its
    // contact (20.w, two half floats): the reach and the coverage, both zero for no contact.
    float3 scatter;
    float scattering;
    float contactReach;
    float contactCoverage;
    float shadowDensity;
    // The material's own cel style (SdfToon, rows 22 to 25), laid out as the pass block's toon members: bands (mode,
    // threshold, softness, specular size), shadow (tint, exposure gain), outline (width, distance, normal, shoulder),
    // ink (tint). A zero mode inherits the world's default style (sdfToonStyle).
    float4 toonBands;
    float4 toonShadow;
    float4 toonOutline;
    float3 toonInk;
    // The flow layer (SdfFlow, rows 27 to 30; sdfApplyFlow): the velocity and the pattern's frequency (a zero frequency
    // says no flow); the stretch along the flow, the cycles the flow clock's period holds, the tilt and the coverage; the
    // revealed colour and the coverage's softness; the revealed roughness and metal, the seed and the control word (the
    // lane plus one in SDF_FLOW_LANE_MASK, SDF_FLOW_WORLD_FRAME for a world-space velocity).
    float4 flowVelocity;
    float4 flowShape;
    float4 flowColor;
    float2 flowResponse;
    uint flowSeed;
    uint flowControl;
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

// A material's first vector in the program words, or false for a material the table does not hold (a screen's
// sentinel, the error material, a table the program words cannot contain).
bool sdfMaterialBase(int material, out uint materialBase) {
    uint4 header = sdfProgramWord(0);

    materialBase = 0u;
    if ((material < 0) || ((uint)material >= SDF_PROGRAM_MATERIAL_COUNT(header))
        || !sdfProgramRange(SDF_PROGRAM_MATERIAL_OFFSET(header), SDF_PROGRAM_MATERIAL_COUNT(header), SDF_MATERIAL_VECTORS_PER_ENTRY)) {
        return false;
    }
    materialBase = SDF_PROGRAM_MATERIAL_OFFSET(header) + SDF_MATERIAL_VECTORS_PER_ENTRY * (uint)material;
    return true;
}
// A material's wrap share alone (row 2's y), zero for a material the table does not hold: the surface pass reads it to
// know how far past the terminator the material's diffuse response reaches.
#ifdef SDF_SURFACE_PASS
float sdfMaterialShadowDemand(int material) {
    uint materialBase;

    if (!sdfMaterialBase(material, materialBase)) return 0.0;
    float demand = -asfloat(sdfWords[materialBase + 2u]).y;
    if (passGroup.shadePrograms != 0u && asuint(sdfWords[materialBase + 32u]).y != 0u)
        demand = asfloat(sdfWords[materialBase + 33u]).x;
    else if (passGroup.shadePrograms != 0u && passGroup.shadeDefault.y != 0u) {
        float4 envelope = passGroup.shadeDemand;
        demand = envelope.z != 0.0 ? -1.0 : -sin(min(1.5707963267948966, asin(saturate(max(-demand, envelope.x))) + envelope.y));
    }
    // A cel material is queried on every side: its band reads a neighbourhood normal (sdfToonGather), which can face the
    // light where the pixel's own normal does not, and an unqueried slot decodes as fully visible, which would light a
    // line of full key light along every edge and terminator.
    float toonMode = asfloat(sdfWords[materialBase + 22u]).x;
    if ((passGroup.toonBands.x >= 0.0) && ((toonMode > 0.0) || ((toonMode == 0.0) && (passGroup.toonBands.x > 0.0)))) demand = -1.0;
    return demand;
}
#endif

// The shadow raster needs only density, before any fragment side effect or receiver fetch.
float sdfMaterialShadowDensity(int material) {
    uint materialBase;
    return sdfMaterialBase(material, materialBase) ? asfloat(sdfWords[materialBase + 25u]).w : 0.0;
}

// The ONE material decode point (KEEP IN SYNC with SdfAppearance.Pack and the stream layout in SdfProgram.cs).
SdfMaterialData sdfMaterialLoad(int material) {
    SdfMaterialData data = (SdfMaterialData)0;
    uint materialBase;
    if (!sdfMaterialBase(material, materialBase)) {
        data.albedo = float3(1.0, 0.0, 1.0);
        data.emissive = 4.0;
        data.roughness = 1.0;
        return data;
    }
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
    data.deposit = asfloat(sdfWords[materialBase + 18u]).rgb;
    data.depositResponse = asfloat(sdfWords[materialBase + 19u]);
    data.weatheringOctave = float2(data.underResponse[0].z, asfloat(sdfWords[materialBase + 18u]).w);
    data.weatheringTurn = asfloat(sdfWords[materialBase + 26u]);
    data.paintFirst = sdfWords[materialBase + 20u].x;
    data.paintCount = sdfWords[materialBase + 20u].y;
    data.emissiveLane = sdfWords[materialBase + 20u].z;
    data.absorption = asfloat(sdfWords[materialBase + 21u]).rgb;
    data.ior = asfloat(sdfWords[materialBase + 21u]).w;
    data.scatter = asfloat(sdfWords[materialBase + 31u]).rgb;
    data.scattering = asfloat(sdfWords[materialBase + 31u]).w;
    data.contactReach = f16tof32(sdfWords[materialBase + 20u].w & 0xFFFFu);
    data.contactCoverage = f16tof32(sdfWords[materialBase + 20u].w >> 16u);
    data.shadowDensity = sdfMaterialShadowDensity(material);
    data.toonBands = asfloat(sdfWords[materialBase + 22u]);
    data.toonShadow = asfloat(sdfWords[materialBase + 23u]);
    data.toonOutline = asfloat(sdfWords[materialBase + 24u]);
    data.toonInk = asfloat(sdfWords[materialBase + 25u]).rgb;
    data.flowVelocity = asfloat(sdfWords[materialBase + 27u]);
    data.flowShape = asfloat(sdfWords[materialBase + 28u]);
    data.flowColor = asfloat(sdfWords[materialBase + 29u]);
    data.flowResponse = asfloat(sdfWords[materialBase + 30u].xy);
    data.flowSeed = sdfWords[materialBase + 30u].z;
    data.flowControl = sdfWords[materialBase + 30u].w;

    return data;
}
// Whether a material is glass: it carries an index of refraction (SdfMaterial.IsGlass).
bool sdfMaterialIsGlass(SdfMaterialData material) {
    return (material.ior > 0.0);
}
// A material's albedo and its paints' range (paintFirst and paintCount, as sdfMaterialLoad reads them), loaded alone so
// the paints draw before the rest of the material is live: zero paints for a material the table does not hold.
float3 sdfMaterialPaints(int material, out uint paintFirst, out uint paintCount) {
    uint materialBase;

    paintFirst = 0u;
    paintCount = 0u;
    if (!sdfMaterialBase(material, materialBase)) {
        return float3(1.0, 0.0, 1.0);
    }

    paintFirst = sdfWords[materialBase + 20u].x;
    paintCount = sdfWords[materialBase + 20u].y;

    return asfloat(sdfWords[materialBase]).rgb;
}
// Scales a lane-driven emissive by its render lane (sdfLane), saturated; `slotLanes` are the shaded surface's frame-slot
// lanes. A static emissive (lane 0 in the packed word, which holds the lane plus one) is untouched.
void sdfApplyEmissiveLane(float4 slotLanes, inout SdfMaterialData material) {
    if (material.emissiveLane != 0u) {
        material.emissive *= saturate(sdfLane(slotLanes, material.emissiveLane - 1u));
    }
}
// Thin wrapper for callers that only shade the base color (debug views, palette ramps).
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
// The GGX lobe's directional albedo under a uniform environment, the split-sum environment BRDF's scale and bias at a
// roughness and view angle (Karis's analytic fit of the UE4 lookup): the environment radiance about the mirror direction
// times this is the lobe's reflection of it. A rough dielectric stays near its f0 at every angle, so a matte surface
// at grazing reflects little; a smooth one rises toward one there.
float3 sdfMaterialEnvironmentBrdf(float3 f0, float roughness, float nDotV) {
    float4 c0 = float4(-1.0, -0.0275, -0.572, 0.022);
    float4 c1 = float4(1.0, 0.0425, 1.04, -0.04);
    float4 r = ((roughness * c0) + c1);
    float a004 = ((min((r.x * r.x), exp2(-9.28 * nDotV)) * r.x) + r.y);
    float2 scaleBias = ((float2(-1.04, 1.04) * a004) + r.zw);

    return ((f0 * scaleBias.x) + scaleBias.y);
}
// The ONE lit-surface shade funnel: a metal-scaled lambert term, an emissive lift, and a fresnel sheen edge-lift.
// `diffuse` is the caller's accumulated irradiance over pi, in nits a white surface would show (ambient, the lights'
// diffuse terms — a float3 so colored lights tint the surface); each light's GGX and clearcoat lobe is its own response
// (sdfLightResponse), added after the shade. The emissive is a luminance in nits the albedo tints. KEEP IN SYNC across
// every caller (passes/sdf-light-stage.hlsli).
float sdfMaterialSheenGain(SdfMaterialData material, float3 normal, float3 rayDirection) {
    if (material.sheen <= 0.0) {
        return 1.0;
    }
    float fresnel = pow(saturate(1.0 - saturate(dot(normal, -rayDirection))), SdfSheenFresnelExponent);
    return (1.0 + (material.sheen * fresnel));
}
float3 sdfMaterialShade(SdfMaterialData material, float3 diffuse, float3 normal, float3 rayDirection) {
    float3 diffuseAlbedo = (material.albedo * (1.0 - material.metal));
    float3 color = (diffuseAlbedo * diffuse);

    if (material.emissive > 0.0) {
        color += (material.albedo * material.emissive);
    }

    return (color * sdfMaterialSheenGain(material, normal, rayDirection));
}
// What a change of `diffuse` light changes in the shade above (sdfMaterialShade), its emission left out: the shade is
// linear in the light, so a change it never saw (the cel step's, which can darken) adds through the same lambert and
// sheen terms.
float3 sdfMaterialDiffuse(SdfMaterialData material, float3 diffuse, float3 normal, float3 rayDirection) {
    return ((material.albedo * (1.0 - material.metal)) * diffuse * sdfMaterialSheenGain(material, normal, rayDirection));
}

#endif
