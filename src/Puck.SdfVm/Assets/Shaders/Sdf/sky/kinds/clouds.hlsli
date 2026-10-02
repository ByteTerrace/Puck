// A cloud kind owns shape and intrinsic coverage. The common wrapper supplies the ordinary light responses.
#ifndef SKY_KIND_CLOUDS_HLSLI
#define SKY_KIND_CLOUDS_HLSLI
#include "../../field/sdf-hash.hlsli"
#include "../../field/sdf-noise.hlsli"

struct SdfSkyCloudSample {
    float3 localDirection;
    float3 normal;
    float2 cellPoint;
    float sinAngle;
    float cosAngle;
    float thickness;
    float alpha;
};

// The positive sphere root, scaled so finite large radii neither square to infinity nor subtract nearly equal terms.
// For R>=1, rsqrt(R) remains normal even at the largest binary32 radius; its square only perturbs 2 below float precision.
float sdfSkyCloudDomeDistance(float elevation, float radius) {
    if (radius < 1.0) {
        float b = radius * elevation;
        return sqrt(b * b + (2.0 * radius + 1.0)) - b;
    }
    float d = rsqrt(radius);
    float a = 2.0 + d * d;
    float s = d * sqrt(a);
    float h = max(elevation, s);
    float yScaled = elevation / h;
    float sScaled = s / h;
    float root = h * sqrt(yScaled * yScaled + sScaled * sScaled);
    return a / (root + elevation);
}

float sdfSkyCloudRadius(float2 layer) {
    float greatest = max(abs(layer.x), abs(layer.y));
    if (greatest == 0.0) return 0.0;
    return greatest * length(layer / greatest);
}
float sdfSkyCloudThickness(float2 cellPoint, SdfSkyCloudsData parameters, uint octaves, inout uint hashes) {
    float warp = sdfPeriodicFbm2(cellPoint + parameters.ShearOffset, parameters.Seed ^ SDF_HASH_STREAM_A, octaves, hashes);
    float density = sdfPeriodicFbm2(cellPoint + parameters.Warp * (warp - 0.5), parameters.Seed, octaves, hashes);
    float threshold = 1.0 - parameters.Coverage;
    float u = saturate((density - threshold) * parameters.InverseSoftness);
    return u * u * (3.0 - 2.0 * u);
}

// Low is one thickness tap with three octaves (24 hashes). Medium/high retain four octaves and the two normal taps
// (96 hashes); each contributing light later adds its own sunward tap (32 hashes), never a selected aggregate sun.
// The resolver has validated finite parameters, positive scale/normal spacing, and the caller's unit direction.
bool sdfPrepareSkyClouds(float3 localDirection, SdfSkyCloudsData parameters, uint quality,
    out SdfSkyCloudSample sample, inout uint hashes) {
    sample = (SdfSkyCloudSample)0;
    if (parameters.Coverage <= 0.0 || localDirection.y <= 0.0) return false;

    float t = sdfSkyCloudDomeDistance(localDirection.y, parameters.DomeRadius);
    float2 layer = localDirection.xz * t;
    float radius = sdfSkyCloudRadius(layer);
    float curl = radius > 1.0 ? 2.0 / (radius + 1.0 / radius) : 2.0 * radius / (1.0 + radius * radius);
    float angle = parameters.SpinAngle + parameters.Curl * curl;
    float sinAngle;
    float cosAngle;
    sincos(angle, sinAngle, cosAngle);
    float2 turned = float2(layer.x * cosAngle - layer.y * sinAngle, layer.x * sinAngle + layer.y * cosAngle);
    float2 cellPoint = turned * parameters.InverseScale + parameters.Offset;
    uint octaves = quality == 0u ? 3u : 4u;
    float thickness = sdfSkyCloudThickness(cellPoint, parameters, octaves, hashes);
    if (thickness <= 0.0) return false;

    float3 normal = float3(0.0, 1.0, 0.0);
    if (quality > 0u) {
        float thicknessX = sdfSkyCloudThickness(cellPoint + float2(parameters.NormalTap, 0.0), parameters, octaves, hashes);
        float thicknessY = sdfSkyCloudThickness(cellPoint + float2(0.0, parameters.NormalTap), parameters, octaves, hashes);
        float3 slope = float3(
            -(thicknessX - thickness) * parameters.InverseNormalTap * parameters.Height, 1.0,
            -(thicknessY - thickness) * parameters.InverseNormalTap * parameters.Height);
        float3 turnedNormal = normalize(slope / max(1.0, max(abs(slope.x), abs(slope.z))));
        // The existing light response reads a normal in the common sky frame, before this kind's wind rotation.
        normal = float3(turnedNormal.x * cosAngle + turnedNormal.z * sinAngle, turnedNormal.y,
            -turnedNormal.x * sinAngle + turnedNormal.z * cosAngle);
    }
    sample.localDirection = localDirection;
    sample.normal = normal;
    sample.cellPoint = cellPoint;
    sample.sinAngle = sinAngle;
    sample.cosAngle = cosAngle;
    sample.thickness = thickness;
    float horizon = saturate(localDirection.y * parameters.InverseHorizonFade);
    sample.alpha = (1.0 - exp(-thickness * parameters.Extinction)) * horizon * horizon * (3.0 - 2.0 * horizon);
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
        float greatest = max(abs(turnedLight.x), abs(turnedLight.y));
        float2 projectedDirection = greatest > 0.0 ? normalize(turnedLight / greatest) : 0.0;
        float thicknessTowardLight = sdfSkyCloudThickness(
            sample.cellPoint + parameters.NormalTap * 2.0 * projectedDirection, parameters, 4u, hashes);
        shadow = 1.0 - parameters.SelfShadow * saturate(thicknessTowardLight - sample.thickness);
        float alignment = saturate(dot(sample.localDirection, localTowardLight));
        float highlight = parameters.SilverExponent == 0.0 ? 1.0 : (alignment > 0.0 ? pow(alignment, parameters.SilverExponent) : 0.0);
        lining = parameters.SilverLining * highlight * (1.0 - sample.thickness);
    }
    return parameters.Color * (1.0 - parameters.AmbientFloor) * diffuse * shadow + weightedColor * lining;
}

// Ambient enters once, rather than once per light. RGB remains straight; the wrapper applies coverage and opacity.
float4 sdfSkyClouds(SdfSkyCloudSample sample, SdfSkyCloudsData parameters, float3 ambient, float3 direct) {
    return float4(parameters.Color * parameters.AmbientFloor * ambient + direct, sample.alpha);
}
#endif
