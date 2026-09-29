// A cloud kind owns shape and intrinsic coverage. The common wrapper supplies the ordinary light responses.
#ifndef SKY_KIND_CLOUDS_HLSLI
#define SKY_KIND_CLOUDS_HLSLI
#include "../../field/sdf-hash.hlsli"
#include "../../field/sdf-noise.hlsli"

struct SdfSkyCloudSample {
    float3 localDirection;
    float3 normal;
    float2 point;
    float sinAngle;
    float cosAngle;
    float thickness;
    float alpha;
};

float sdfSkyCloudThickness(float2 point, SdfSkyCloudsData parameters, uint octaves, inout uint hashes) {
    float warp = sdfPeriodicFbm2(point + parameters.ShearOffset, parameters.Seed ^ SDF_HASH_STREAM_A, octaves, hashes);
    float density = sdfPeriodicFbm2(point + parameters.Warp * (warp - 0.5), parameters.Seed, octaves, hashes);
    float threshold = 1.0 - parameters.Coverage;
    return smoothstep(threshold, threshold + parameters.Softness, density);
}

// Low is one thickness tap with three octaves (24 hashes). Medium/high retain four octaves and the two normal taps
// (96 hashes); each contributing light later adds its own sunward tap (32 hashes), never a selected aggregate sun.
// The resolver has validated finite parameters, positive scale/normal spacing, and the caller's unit direction.
bool sdfPrepareSkyClouds(float3 localDirection, SdfSkyCloudsData parameters, uint quality,
    out SdfSkyCloudSample sample, inout uint hashes) {
    sample = (SdfSkyCloudSample)0;
    if (parameters.Coverage <= 0.0 || localDirection.y <= 0.0) return false;

    float b = parameters.DomeRadius * localDirection.y;
    float t = sqrt(b * b + (2.0 * parameters.DomeRadius + 1.0)) - b;
    float2 layer = localDirection.xz * t;
    float radius = length(layer);
    float angle = parameters.SpinAngle + parameters.Curl * (2.0 * radius / (1.0 + radius * radius));
    float sinAngle;
    float cosAngle;
    sincos(angle, sinAngle, cosAngle);
    float2 turned = float2(layer.x * cosAngle - layer.y * sinAngle, layer.x * sinAngle + layer.y * cosAngle);
    float2 point = turned / max(parameters.Scale, 1e-3) + parameters.Offset;
    uint octaves = quality == 0u ? 3u : 4u;
    float thickness = sdfSkyCloudThickness(point, parameters, octaves, hashes);
    if (thickness <= 0.0) return false;

    float3 normal = float3(0.0, 1.0, 0.0);
    if (quality > 0u) {
        float thicknessX = sdfSkyCloudThickness(point + float2(parameters.NormalTap, 0.0), parameters, octaves, hashes);
        float thicknessY = sdfSkyCloudThickness(point + float2(0.0, parameters.NormalTap), parameters, octaves, hashes);
        float3 turnedNormal = normalize(float3(
            -(thicknessX - thickness) / parameters.NormalTap * parameters.Height, 1.0,
            -(thicknessY - thickness) / parameters.NormalTap * parameters.Height));
        // The existing light response reads a normal in the common sky frame, before this kind's wind rotation.
        normal = float3(turnedNormal.x * cosAngle + turnedNormal.z * sinAngle, turnedNormal.y,
            -turnedNormal.x * sinAngle + turnedNormal.z * cosAngle);
    }
    sample.localDirection = localDirection;
    sample.normal = normal;
    sample.point = point;
    sample.sinAngle = sinAngle;
    sample.cosAngle = cosAngle;
    sample.thickness = thickness;
    sample.alpha = (1.0 - exp(-thickness * parameters.Extinction)) * smoothstep(0.0, parameters.HorizonFade, localDirection.y);
    return true;
}

// The wrapper calls sdfLightResponse once per contributing light, in authored order, then supplies its diffuse term
// and the same light's local direction and weighted color. The module neither selects nor branches on light kinds.
float3 sdfSkyCloudLight(SdfSkyCloudSample sample, SdfSkyCloudsData parameters, uint quality,
    float3 diffuse, float3 localTowardLight, float3 weightedColor, inout uint hashes) {
    float shadow = 1.0;
    float lining = 0.0;
    if (quality > 0u) {
        float2 turnedLight = float2(
            localTowardLight.x * sample.cosAngle - localTowardLight.z * sample.sinAngle,
            localTowardLight.x * sample.sinAngle + localTowardLight.z * sample.cosAngle);
        float thicknessTowardLight = sdfSkyCloudThickness(
            sample.point + parameters.NormalTap * 2.0 * normalize(turnedLight + 1e-5), parameters, 4u, hashes);
        shadow = 1.0 - parameters.SelfShadow * saturate(thicknessTowardLight - sample.thickness);
        lining = parameters.SilverLining * pow(saturate(dot(sample.localDirection, localTowardLight)),
            parameters.SilverExponent) * (1.0 - sample.thickness);
    }
    return parameters.Color * (1.0 - parameters.AmbientFloor) * diffuse * shadow + weightedColor * lining;
}

// Ambient enters once, rather than once per light. RGB remains straight; the wrapper applies coverage and opacity.
float4 sdfSkyClouds(SdfSkyCloudSample sample, SdfSkyCloudsData parameters, float3 ambient, float3 direct) {
    return float4(parameters.Color * parameters.AmbientFloor * ambient + direct, sample.alpha);
}
#endif
