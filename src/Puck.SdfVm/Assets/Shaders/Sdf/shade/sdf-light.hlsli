// The one light interface: every light the light stage walks, a light of the lights table (sdfLights) or a bound
// screen's area light, is one SdfLightSource, and sdfLightResponse answers what it adds at a shaded surface. The stage folds the
// responses in the lights' order: the diffuse terms into the radiance the material shade lights by, the rim and specular
// terms after the shade, and the attenuations into one factor on the reflected light.
#ifndef SHADE_SDF_LIGHT_HLSLI
#define SHADE_SDF_LIGHT_HLSLI
#ifdef SDF_VIEWS_PASS

// A bound screen's area light, outside the generated SDF_LIGHT_* kinds, which name the lights table's.
static const uint SdfLightScreen = 0x100u;

struct SdfLightSource {
    uint kind;
    float3 color;
    // A screen's glow strength (its screen-light row's alpha).
    float weight;
    // A directional's unit direction toward the light; a point's, an occluder's or a screen's world position.
    float3 position;
    // A hemisphere's gradient, a rim's exponent, a point's falloff radius or an occluder's radius.
    float param;
    // A screen's face normal.
    float3 facing;
    // The table index used to find this light's own visibility; screens have no shadow slot.
    int index;
};

// The surface a light answers at: its point, lit normal and camera ray, its material, its ambient occlusion (a wrapped
// material's already relaxed toward 1) and each stable and incoming light's shadow visibility.
struct SdfShadeSurface {
    float3 position;
    float3 normal;
    float3 rayDirection;
    SdfMaterialData material;
    float ambientOcclusion;
    float4 shadowVisibility;
    float2 incomingVisibility;
};

// Each participant scales only its own occlusion deficit. Unslotted directionals retain ambient occlusion.
float sdfLightVisibility(int lightIndex, float4 stable, float2 incoming, float ambientOcclusion) {
    if (lightIndex < 0) {
        return ambientOcclusion;
    }
#if SDF_SHADOW_FADE_SLOTS > 0
    [loop]
    for (uint fade = 0u; (fade < min(passGroup.shadowFadeCount, (uint)SDF_SHADOW_FADE_SLOTS)); fade++) {
        SdfShadowHandoff handoff = sdfShadowHandoffs[fade];
        if (lightIndex == handoff.Outgoing) {
            return (1.0 - ((1.0 - stable[handoff.Slot]) * (1.0 - handoff.Weight)));
        }
        if (lightIndex == handoff.Incoming) {
            return (1.0 - ((1.0 - incoming[fade]) * handoff.Weight));
        }
    }
#endif
    [loop]
    for (uint shadowSlot = 0u; (shadowSlot < passGroup.shadowSlotCount); shadowSlot++) {
        if (lightIndex == passGroup.shadowSlots[shadowSlot]) {
            return stable[shadowSlot];
        }
    }
    return ambientOcclusion;
}

// What one light adds at a surface: its diffuse term, its specular lobe and its rim brighten, and the factor it scales
// reflected light by (1 unless it occludes).
struct SdfLightResponse {
    float3 diffuse;
    float3 specular;
    float3 rim;
    float attenuation;
};

// The lights the stage walks: the lights table's, then one slot per screen up to the highest bound one while screen
// lights are on.
uint sdfLightCount() {
    uint count = passGroup.lightCount;
#ifdef SDF_SCREEN_SOURCES
    if (!worldScreenLightsDisabled()) {
        count += screenLightLoopBound();
    }
#endif
    return count;
}
// The light at `index` of the walk, or false for a screen slot whose source is not bound this frame.
bool sdfLightAt(uint index, out SdfLightSource light) {
    light = (SdfLightSource)0;
    light.index = -1;

    uint tableCount = passGroup.lightCount;

    if (index < tableCount) {
        SdfLight record = sdfLights[index];

        light.kind = record.Kind;
        light.color = record.Color;
        light.weight = record.Weight;
        light.position = (((record.Kind == SDF_LIGHT_POINT) || (record.Kind == SDF_LIGHT_OCCLUDER))
            ? worldPointLightPosition(record)
            : record.Direction);
        light.param = record.Param;
        light.index = (int)index;

        return true;
    }

#ifdef SDF_SCREEN_SOURCES
    uint screenIndex = (index - tableCount);

    if (!screenSourceBound(screenIndex)) {
        return false;
    }

    // right and up are orthonormal (SdfScreenSurface); the normalize absorbs upload drift.
    ScreenSurfaceData surface = worldScreenSurface(screenIndex);

    light.kind = SdfLightScreen;
    light.color = sdfScreenLights[screenIndex].rgb;
    light.weight = sdfScreenLights[screenIndex].a;
    light.position = surface.origin.xyz;
    light.facing = normalize(cross(surface.right.xyz, surface.up.xyz));

    return true;
#else
    return false;
#endif
}
// What `light` adds at `surface`:
// - a directional its wrapped Lambert term under its own shadow visibility;
// - a hemisphere its floor plus its gradient along the normal's height under ambient occlusion;
// - a point its wrapped Lambert term and its own GGX lobe under an inverse-square falloff and ambient occlusion;
// - a rim its view-dependent silhouette brighten;
// - an occluder a Gaussian dimming of reflected light, facing the surface;
// - a screen its glow toward what sits in front of its face, under an inverse-square falloff.
// A wrap of 0 reduces each wrapped term to max(n.l, 0).
SdfLightResponse sdfLightResponse(SdfLightSource light, SdfShadeSurface surface) {
    SdfLightResponse response;

    response.diffuse = float3(0.0, 0.0, 0.0);
    response.specular = float3(0.0, 0.0, 0.0);
    response.rim = float3(0.0, 0.0, 0.0);
    response.attenuation = 1.0;

    float3 normal = surface.normal;

    if (light.kind == SDF_LIGHT_DIRECTIONAL) {
        float lambert = sdfWrapDiffuse(dot(normal, light.position), surface.material.wrap);
        float occlusion = sdfLightVisibility(light.index, surface.shadowVisibility, surface.incomingVisibility, surface.ambientOcclusion);

        response.diffuse = (light.color * ((light.weight * lambert) * occlusion));
    } else if (light.kind == SDF_LIGHT_HEMISPHERE) {
        float ambient = (light.weight + (light.param * normal.y));

        response.diffuse = (light.color * (ambient * surface.ambientOcclusion));
    } else if (light.kind == SDF_LIGHT_POINT) {
        float3 toLight = (light.position - surface.position);
        float pointDistance = length(toLight);
        float3 pointDirection = (toLight / max(pointDistance, 1.0e-4));
        float pointRatio = (pointDistance / max(light.param, 1.0e-3));
        float pointFalloff = (light.weight / (1.0 + (pointRatio * pointRatio)));
        float pointLambert = sdfWrapDiffuse(dot(normal, pointDirection), surface.material.wrap);

        response.diffuse = (light.color * ((pointFalloff * pointLambert) * surface.ambientOcclusion));
        response.specular = (light.color * sdfMaterialSpecular(surface.material, normal, -surface.rayDirection, pointDirection, (pointFalloff * surface.ambientOcclusion)));
    } else if (light.kind == SDF_LIGHT_RIM) {
        response.rim = ((light.weight * light.color) * pow((1.0 - saturate(dot(normal, -surface.rayDirection))), light.param));
    } else if (light.kind == SDF_LIGHT_OCCLUDER) {
        if (light.weight > 0.0) {
            float3 delta = (light.position - surface.position);
            float distanceSquared = dot(delta, delta);
            float facing = ((distanceSquared > 1.0e-12) ? saturate(dot(normal, (delta * rsqrt(distanceSquared)))) : 1.0);
            float radius = max(light.param, 1.0e-6);

            response.attenuation = (1.0 - saturate(((light.weight * exp((-distanceSquared / (radius * radius)))) * facing)));
        }
    } else if (light.kind == SdfLightScreen) {
        float3 toLight = (light.position - surface.position);
        float distanceSquared = max(dot(toLight, toLight), ScreenLightMinDistanceSquared);
        float3 lightDirection = (toLight * rsqrt(distanceSquared));
        float facing = (max(dot(normal, lightDirection), 0.0) * saturate(dot(light.facing, -lightDirection)));
        float attenuation = (1.0 / (1.0 + (ScreenLightFalloff * distanceSquared)));

        response.diffuse = (light.color * ((light.weight * facing) * attenuation));
    }

    return response;
}

#endif
#endif
