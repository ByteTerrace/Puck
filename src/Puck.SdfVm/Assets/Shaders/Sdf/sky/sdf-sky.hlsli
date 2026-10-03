// The sky's layer stack, read from the sky block (sdfSky) and its layer table (sdfSkyLayers): what every layer shares
// (the sky frame, its rotation, mask, opacity and blend), the kinds' modules through the generated kind table
// (isa/sdf-sky-kinds.hlsli and sdf-sky-kind-table.hlsli, one module a kind under kinds/), and the walks the sky,
// composite and environment passes make over the stack in its authored order. The stack is cut into runs without
// reordering it: a maximal sequence of consecutive field layers is one field run, summarized as the affine map
// d' = M·d + B it applies to the colour beneath, and the point layers between field runs are evaluated one by one at each
// pixel. The lowest field run, when the stack
// opens with one, composes over nothing and is its offset alone; the field runs above it (sdfSky[0].UpperRuns) are each
// a scale and an offset. Each layer counts its own evaluations in its detail row (SdfSkyLayer.Detail); a run's texels
// written and loaded count in the run's row (0 the lowest field run, then the upper runs).
#ifndef SKY_SDF_SKY_HLSLI
#define SKY_SDF_SKY_HLSLI
#include "../field/sdf-octahedral.hlsli"
#include "../field/sdf-hash.hlsli"
#include "../field/sdf-noise.hlsli"
#include "../isa/sdf-sky-kinds.hlsli"

// Where a layer is evaluated: the world direction (a disc sits where its light shines from), the layer-frame direction
// (the sky frame, then the layer's rotation), and the sky's quality tier, below SDF_SKY_TIER_HIGH of which a kind takes
// its reduced form.
struct SdfSkySample {
    float3 world;
    float3 local;
    uint tier;
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

// Each kind's module and the evaluation switch (sdfSkyKindEvaluate), generated from the kind table. Its modules read the
// declarations above.
#include "sdf-sky-kind-table.hlsli"

// A layer's colour and weight at a world direction: its kind's value, its alpha scaled by its opacity and its mask. A
// direction the mask leaves out evaluates nothing.
float4 sdfSkyLayerValue(SdfSkyLayer layer, float3 world) {
    float3 sky = sdfSkyFrameDirection(world);
    float mask = (layer.Opacity * sdfSkyMaskWeight(layer, sky));

    if (mask <= 0.0) {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    SdfSkySample sample;

    sample.world = world;
    sample.local = sdfSkyRotate(sky, layer.Rotation);
    sample.tier = sdfSky[0].Quality;

    float4 value = sdfSkyKindEvaluate(layer, sample);

    return float4(value.rgb, (value.a * mask));
}
// Applies one layer at a world direction to the colour beneath it.
float3 sdfSkyApplyLayer(SdfSkyLayer layer, float3 world, float3 beneath) {
    float4 value = sdfSkyLayerValue(layer, world);
    float3 scale;
    float3 offset;

    if (value.a <= 0.0) {
        return beneath;
    }

    sdfSkyAffine(layer.Blend, value.rgb, value.a, scale, offset);

    return ((scale * beneath) + offset);
}
// Whether the camera sees a layer.
bool sdfSkyLayerSeen(SdfSkyLayer layer) {
    return ((layer.Visibility & SDF_SKY_VISIBILITY_CAMERA) != 0u);
}
// The sky's field runs at a world direction, as the sky pass writes them: the lowest field run's offset (black when the
// stack opens with a point layer), and each upper field run's scale and offset, in authored order. Only the layers the
// camera sees take part.
void sdfSkyFieldRuns(float3 world, out float3 base, out float3 scales[SDF_SKY_MAX_UPPER_FIELD_RUNS], out float3 offsets[SDF_SKY_MAX_UPPER_FIELD_RUNS]) {
    SdfSkyBlock sky = sdfSky[0];
    int run = -1;
    uint upper = 0u;
    bool inField = false;
    bool started = false;

    base = float3(0.0, 0.0, 0.0);
    [unroll] for (uint slot = 0u; (slot < SDF_SKY_MAX_UPPER_FIELD_RUNS); slot++) {
        scales[slot] = float3(1.0, 1.0, 1.0);
        offsets[slot] = float3(0.0, 0.0, 0.0);
    }

    [loop]
    for (uint index = 0u; (index < sky.LayerCount); index++) {
        SdfSkyLayer layer = sdfSkyLayers[index];

        if (!sdfSkyLayerSeen(layer)) {
            continue;
        }
        if (!sdfSkyKindIsField(layer.Kind)) {
            inField = false;
            started = true;

            continue;
        }
        if (!inField) {
            // A field run that opens the stack is the base; every later one takes the next upper slot.
            run = (started ? (int)(upper++) : -1);
            inField = true;
        }
        started = true;

        float4 value = sdfSkyLayerValue(layer, world);

        if (value.a <= 0.0) {
            continue;
        }

        float3 scale;
        float3 offset;

        sdfSkyAffine(layer.Blend, value.rgb, value.a, scale, offset);
        if (run < 0) {
            base = ((scale * base) + offset);
        } else if (run < (int)SDF_SKY_MAX_UPPER_FIELD_RUNS) {
            // Applying (M1, B1) and then (M2, B2) is d -> M2·(M1·d + B1) + B2.
            scales[run] = (scale * scales[run]);
            offsets[run] = ((scale * offsets[run]) + offset);
        }
    }
}
// The sky the camera sees at a world direction, composed in the stack's authored order over black: the lowest field run's
// offset, then every point layer evaluated here and every upper field run applied from its summary. With no summaries
// (`summarized` false) every field layer is evaluated here too.
float3 sdfSkyCompose(float3 world, bool summarized, float3 base, float3 scales[SDF_SKY_MAX_UPPER_FIELD_RUNS], float3 offsets[SDF_SKY_MAX_UPPER_FIELD_RUNS]) {
    SdfSkyBlock sky = sdfSky[0];
    float3 color = (summarized ? base : float3(0.0, 0.0, 0.0));
    uint upper = 0u;
    bool inField = false;
    bool started = false;

    [loop]
    for (uint index = 0u; (index < sky.LayerCount); index++) {
        SdfSkyLayer layer = sdfSkyLayers[index];

        if (!sdfSkyLayerSeen(layer)) {
            continue;
        }
        if (!sdfSkyKindIsField(layer.Kind)) {
            inField = false;
            started = true;
            color = sdfSkyApplyLayer(layer, world, color);

            continue;
        }
        if (!summarized) {
            started = true;
            color = sdfSkyApplyLayer(layer, world, color);

            continue;
        }
        if (!inField) {
            // The base is already the colour; each later field run applies its summary once, where it opens.
            if (started && (upper < SDF_SKY_MAX_UPPER_FIELD_RUNS)) {
                color = ((scales[upper] * color) + offsets[upper]);
                upper++;
            }
            inField = true;
        }
        started = true;
    }

    return color;
}
// The sky the lighting sees at a world direction: the layers the lighting sees, but a disc, composed over black in their
// authored order (SdfSkyEnvironment.Evaluate), which the environment map holds.
float3 sdfSkyEnvironmentColor(float3 world) {
    SdfSkyBlock sky = sdfSky[0];
    float3 color = float3(0.0, 0.0, 0.0);

    [loop]
    for (uint index = 0u; (index < sky.LayerCount); index++) {
        SdfSkyLayer layer = sdfSkyLayers[index];

        if (((layer.Visibility & SDF_SKY_VISIBILITY_LIGHTING) == 0u) || (layer.Kind == SDF_SKY_KIND_DISC)) {
            continue;
        }

        color = sdfSkyApplyLayer(layer, world, color);
    }

    return color;
}
#endif
