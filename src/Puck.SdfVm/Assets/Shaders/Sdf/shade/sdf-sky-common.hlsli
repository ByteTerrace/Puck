#ifndef SHADE_SDF_SKY_COMMON_HLSLI
#define SHADE_SDF_SKY_COMMON_HLSLI
#include "../isa/sdf-sky-kinds.hlsli"
// Where a layer is evaluated: the world direction (a disc sits where its light shines from), the layer-frame direction
// (the sky frame, then the layer's rotation), and the sky's quality tier, below SDF_SKY_TIER_HIGH of which a kind takes
// its reduced form, and the packed layer ordinal indexing this consumer's fitted infinity data.
struct SdfSkySample {
    float3 world;
    float3 local;
    uint tier;
    uint layer;
};

// A world direction in the sky frame: its components along the frame's axes (SdfSkyEnvironment.FrameDirection).
float3 sdfSkyFrameDirection(float3 direction) {
    SdfSkyBlock sky = sdfSky[0];

    return float3(dot(direction, sky.FrameRight), dot(direction, sky.FrameUp), dot(direction, sky.FrameForward));
}
// A direction rotated by a unit quaternion: v + 2·q × (q × v + w·v) (SdfSkyEnvironment.Rotate).
float3 sdfSkyRotate(float3 direction, float4 rotation) {
    return (direction + (2.0 * cross(rotation.xyz, (cross(rotation.xyz, direction) + (rotation.w * direction)))));
}
// A step from zero below an edge to one at it, widened downward over a soft width; a hard step without one.
float sdfSkyRise(float edge, float soft, float value) {
    if (soft <= 0.0) {
        return ((value >= edge) ? 1.0 : 0.0);
    }

    float t = saturate((value - (edge - soft)) / soft);

    return ((t * t) * (3.0 - (2.0 * t)));
}
// A layer's mask weight at a sky-frame direction, in [0, 1] (SdfSkyEnvironment.MaskWeight).
float sdfSkyMaskWeight(SdfSkyLayer layer, float3 sky) {
    float soft = layer.MaskSoftness;

    if (layer.Mask == SDF_SKY_MASK_ELEVATION) {
        return (sdfSkyRise(layer.MaskBand.x, soft, sky.y) * (1.0 - sdfSkyRise((layer.MaskBand.y + soft), soft, sky.y)));
    }
    if (layer.Mask == SDF_SKY_MASK_CONE) {
        return sdfSkyRise(layer.MaskBand.w, soft, dot(sky, layer.MaskBand.xyz));
    }

    return 1.0;
}
// The affine map a layer of colour c and alpha a applies to the colour beneath it, per channel: over is a·c + (1 − a)·d,
// add d + a·c, multiply d scaled toward c by a, screen d lifted toward one by a·c (SdfSkyRuns.Affine).
void sdfSkyAffine(uint blend, float3 color, float alpha, out float3 scale, out float3 offset) {
    if (blend == SDF_SKY_BLEND_ADD) {
        scale = float3(1.0, 1.0, 1.0);
        offset = (alpha * color);
    } else if (blend == SDF_SKY_BLEND_MULTIPLY) {
        scale = ((float3(1.0, 1.0, 1.0) - (alpha * float3(1.0, 1.0, 1.0))) + (alpha * color));
        offset = float3(0.0, 0.0, 0.0);
    } else if (blend == SDF_SKY_BLEND_SCREEN) {
        scale = (float3(1.0, 1.0, 1.0) - (alpha * color));
        offset = (alpha * color);
    } else {
        scale = (1.0 - alpha).xxx;
        offset = (alpha * color);
    }
}

#endif
