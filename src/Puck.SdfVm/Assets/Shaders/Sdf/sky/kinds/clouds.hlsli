// The clouds kind: a heightfield of cloud on a dome, a spherical shell of radius DomeRadius + 1 about a centre DomeRadius
// below the camera, unit height overhead. A layer-frame direction's layer point is where its ray meets that shell
// (t = -R·d.y + sqrt(R²·d.y² + 2R + 1): 1 overhead, sqrt(2R + 1) at the horizon), so the layer compresses smoothly toward
// the horizon and no direction needs a clamp. The wind acts on that point before the noise reads it: the layer turns
// about the zenith by the host-integrated spin angle, is wound by the Coriolis curl (an extra rotation of
// curl · 2r/(1+r²), zero at the zenith, peaking at 45° elevation), is scaled by the cell size and slid by the
// host-integrated drift. The thickness at a point is a domain-warped fractal sum (field/sdf-noise.hlsli: a first sum
// bends the second's domain by Warp), the shaping sum read at its own host-integrated shear offset, thresholded at
// (1 − coverage) over the softness. At SDF_SKY_TIER_HIGH the sums take Octaves octaves and below it three. Above
// SDF_SKY_TIER_LOW volume is read from that heightfield with four thickness taps: the centre and two offset taps give a
// surface normal (Height tall per unit thickness) lit against the light; a fourth tap toward the light finds a taller
// neighbour shadowing this point (SelfShadow); the light seen through a thin edge lines it (SilverLining). At
// SDF_SKY_TIER_LOW the one centre tap shades the cloud flat. The thickness sets the opacity through Beer's law
// (1 − exp(−t·Extinction)), and the layer fades to nothing in the last HorizonFade of height and is never drawn below the
// horizon. Each fractal octave hashes four lattice corners, which count.
#ifndef SKY_KINDS_CLOUDS_HLSLI
#define SKY_KINDS_CLOUDS_HLSLI

float sdfSkyCloudThickness(SdfSkyClouds clouds, SdfSkyLayer layer, float2 p, float threshold, uint octaves) {
    float warp = sdfPeriodicFbm2((p + clouds.ShearOffset), (clouds.Seed ^ 0x9E3779B9u), octaves);
    float density = sdfPeriodicFbm2((p + (clouds.Warp * (warp - 0.5))), clouds.Seed, octaves);

    puckCountDetail(layer.Detail, 0u, 0u, 0u, (8u * octaves), 0u);

    return smoothstep(threshold, (threshold + clouds.Softness), density);
}
float4 sdfSkyCloudsLayer(SdfSkyClouds clouds, SdfSkyLayer layer, SdfSkySample sample) {
    float3 direction = sample.local;

    if ((clouds.Coverage <= 0.0) || (direction.y <= 0.0)) {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    puckCountDetail(layer.Detail, 0u, 0u, 1u, 0u, 0u);

    uint octaves = ((sample.tier >= SDF_SKY_TIER_HIGH) ? clamp(clouds.Octaves, 1u, 8u) : 3u);
    float b = (clouds.DomeRadius * direction.y);
    float t = (sqrt((b * b) + ((2.0 * clouds.DomeRadius) + 1.0)) - b);
    float2 layerPoint = (direction.xz * t);
    float radius = length(layerPoint);
    float angle = (clouds.SpinAngle + (clouds.Curl * ((2.0 * radius) / (1.0 + (radius * radius)))));
    float sinAngle;
    float cosAngle;

    sincos(angle, sinAngle, cosAngle);

    float2 turned = float2(((layerPoint.x * cosAngle) - (layerPoint.y * sinAngle)), ((layerPoint.x * sinAngle) + (layerPoint.y * cosAngle)));
    float2 p = ((turned / max(clouds.Scale, 1e-3)) + clouds.DriftOffset);
    float threshold = (1.0 - clouds.Coverage);
    float thickness = sdfSkyCloudThickness(clouds, layer, p, threshold, octaves);

    if (thickness <= 0.0) {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    float alpha = ((1.0 - exp(-(thickness * clouds.Extinction))) * smoothstep(0.0, clouds.HorizonFade, direction.y));

    if (sample.tier == SDF_SKY_TIER_LOW) {
        return float4(clouds.Color, alpha);
    }

    // The light in the layer frame, then in the layer's turned frame (the rotation the layer point took), so the lighting
    // follows the wind.
    float3 sun = sdfSkyRotate(sdfSkyFrameDirection(clouds.LightDirection), layer.Rotation);
    float2 sunTurned = float2(((sun.x * cosAngle) - (sun.z * sinAngle)), ((sun.x * sinAngle) + (sun.z * cosAngle)));
    float thicknessX = sdfSkyCloudThickness(clouds, layer, (p + float2(clouds.NormalTap, 0.0)), threshold, octaves);
    float thicknessY = sdfSkyCloudThickness(clouds, layer, (p + float2(0.0, clouds.NormalTap)), threshold, octaves);
    float thicknessSunward = sdfSkyCloudThickness(clouds, layer, (p + (clouds.NormalTap * 2.0 * normalize(sunTurned + 1e-5))), threshold, octaves);
    float3 normal = normalize(float3(-((thicknessX - thickness) / clouds.NormalTap) * clouds.Height, 1.0, -((thicknessY - thickness) / clouds.NormalTap) * clouds.Height));
    float diffuse = saturate(dot(normal, normalize(float3(sunTurned.x, sun.y, sunTurned.y))));
    float shadow = (1.0 - (clouds.SelfShadow * saturate(thicknessSunward - thickness)));
    float lining = ((clouds.SilverLining * pow(saturate(dot(direction, sun)), 8.0)) * (1.0 - thickness));
    float3 shade = ((clouds.Color * (lerp(0.45, 1.0, diffuse) * shadow)) + (clouds.LightColor * lining));

    return float4(shade, alpha);
}
#endif
