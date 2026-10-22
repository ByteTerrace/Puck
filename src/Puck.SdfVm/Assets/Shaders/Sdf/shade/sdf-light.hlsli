// The one light interface: every light the light stage walks, a light of the lights table (sdfLights) the view shades or
// a bound screen's area light, is one SdfLightSource, and sdfLightResponse answers what it adds at a shaded surface. The
// stage folds the responses in the lights' order: the diffuse terms into the radiance the material shade lights by, the
// rim and specular terms after the shade, and the attenuations into one factor on the reflected light. Every term is
// photometric (SdfPhotometry): a diffuse term is the illuminance over pi a white surface would show, in nits, and a
// specular term the luminance its lobe reflects.
#ifndef SHADE_SDF_LIGHT_HLSLI
#define SHADE_SDF_LIGHT_HLSLI
#ifdef SDF_VIEWS_PASS
#include "sdf-mesh-irradiance.hlsli"

// A bound screen's area light, outside the generated SDF_LIGHT_* kinds, which name the lights table's.
static const uint SdfLightScreen = 0x100u;

struct SdfLightSource {
    uint kind;
    float3 color;
    // A directional's illuminance over pi, a point's intensity over pi, a rim's luminance, an occluder's attenuation, a
    // screen's face area over pi.
    float weight;
    // A directional's unit direction toward the light; a point's, an occluder's or a screen's world position.
    float3 position;
    // A rim's exponent, a point's range, an occluder's radius or a screen's face area.
    float param;
    // A screen's face normal.
    float3 facing;
    // The table index used to find this light's own visibility; screens have no shadow slot.
    int index;
    // The table record, for a point's falloff and cone.
    SdfLight record;
};

// The surface a light answers at: its point, lit normal and camera ray, its material, its ambient occlusion (a wrapped
// material's already relaxed toward 1), each stable slot's shadow visibility, and the irradiance volume's probes about
// it, which occlude a traced point or spot light (probed is false while the volume is off, and every such light goes
// unoccluded).
struct SdfShadeSurface {
    float3 position;
    float3 normal;
    float3 rayDirection;
    SdfMaterialData material;
    float ambientOcclusion;
    float4 shadowVisibility;
    bool probed;
    SdfMeshProbes probes;
};

// A light's shadow visibility. A handoff fades a stable slot from its outgoing light to its incoming one: the outgoing
// light's own occlusion deficit, which the slot's cascades still render, fades out with the handoff's weight, and the
// incoming light, which no cascade renders until it holds the slot, shades unshadowed. Unslotted directionals retain
// ambient occlusion.
float sdfLightVisibility(int lightIndex, float4 stable, float ambientOcclusion) {
    if (lightIndex < 0) {
        return ambientOcclusion;
    }
    [loop]
    for (uint fade = 0u; fade < (uint)SDF_SHADOW_FADE_CAPACITY; fade++) {
        if (fade >= worldShadowHandoffCount()) { break; }
        SdfShadowHandoff handoff = sdfShadowHandoffs[fade];
        if (handoff.Slot < 0 || (uint)handoff.Slot >= SDF_MAX_SHADOW_SLOTS) { continue; }
        if (lightIndex == handoff.Outgoing) {
            return (1.0 - ((1.0 - stable[handoff.Slot]) * (1.0 - handoff.Weight)));
        }
        if (lightIndex == handoff.Incoming) {
            return 1.0;
        }
    }
    [loop]
    for (uint shadowSlot = 0u; shadowSlot < SDF_MAX_SHADOW_SLOTS; shadowSlot++) {
        if (shadowSlot >= worldShadowStableCount()) { break; }
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
    uint count = worldLightCount();
#ifdef SDF_SCREEN_SOURCES
    if (!worldScreenLightsDisabled()) {
        count += min(screenLightLoopBound(), SDF_MAX_SCREEN_SURFACES);
    }
#endif
    return count;
}
// The light at `index` of the walk, or false for a table light the view does not shade (worldLightSelected) or a screen
// slot whose source is not bound this frame.
bool sdfLightAt(uint index, out SdfLightSource light) {
    light = (SdfLightSource)0;
    light.index = -1;

    uint tableCount = worldLightCount();

    if (index < tableCount) {
        if (!worldLightSelected(index)) {
            return false;
        }

        SdfLight record = sdfLights[index];

        light.kind = record.Kind;
        light.color = record.Color;
        light.weight = record.Weight;
        light.position = (((record.Kind == SDF_LIGHT_POINT) || (record.Kind == SDF_LIGHT_OCCLUDER))
            ? worldPointLightPosition(record)
            : record.Direction);
        light.param = record.Param;
        light.index = (int)index;
        light.record = record;

        return true;
    }

#ifdef SDF_SCREEN_SOURCES
    uint screenIndex = (index - tableCount);
    uint surfaceRows;
    uint mappingRows;
    uint emissionRows;
    uint stride;
    screenSurfaces.GetDimensions(surfaceRows, stride);
    screenMappings.GetDimensions(mappingRows, stride);
    sdfScreenLights.GetDimensions(emissionRows, stride);
    if (screenIndex >= min(screenLightLoopBound(), SDF_MAX_SCREEN_SURFACES)
        || screenIndex >= surfaceRows / WorldScreenSurfaceRows
        || screenIndex >= mappingRows / WorldScreenMappingRows
        || screenIndex >= emissionRows) { return false; }

    if (!screenSourceBound(screenIndex)) {
        return false;
    }

    // right and up are orthonormal (SdfScreenSurface); the normalize absorbs upload drift.
    ScreenSurfaceData surface = worldScreenSurface(screenIndex);

    light.kind = SdfLightScreen;
    // The face's mean luminance, in nits (sdf-screen-emission.comp.hlsl).
    light.color = sdfScreenLights[screenIndex].rgb;
    light.param = ((4.0 * surface.right.w) * surface.up.w);
    light.weight = (light.param / SDF_PI);
    light.position = surface.origin.xyz;
    light.facing = normalize(cross(surface.right.xyz, surface.up.xyz));

    return true;
#else
    return false;
#endif
}
// What `light` adds at `surface`:
// - a directional its wrapped Lambert term and its own GGX lobe under its shadow visibility;
// - a point or spot its wrapped Lambert term and its own GGX lobe under the windowed inverse square and the spot's cone
//   (sdfPointReach), ambient occlusion and, for a static light the irradiance volume traced (its Shadows flag,
//   SdfLight.IsTraced), the share of the probes about the surface that see it (sdfMeshLightVisibility);
// - a rim its view-dependent silhouette brighten;
// - an occluder a Gaussian dimming of reflected light, facing the surface;
// - a screen its glow toward what sits in front of its face: a Lambertian face of luminance L and area A casts
//   E = L * A * cos / (d^2 + A / pi) on a surface facing it, exact on the axis of a disc of that area.
// A specular lobe's scale is the light's illuminance, pi times its weight, since sdfMaterialSpecular answers the BRDF
// times the cosine. A wrap of 0 reduces each wrapped term to max(n.l, 0).
SdfLightResponse sdfLightResponse(SdfLightSource light, SdfShadeSurface surface) {
    SdfLightResponse response;

    response.diffuse = float3(0.0, 0.0, 0.0);
    response.specular = float3(0.0, 0.0, 0.0);
    response.rim = float3(0.0, 0.0, 0.0);
    response.attenuation = 1.0;

    float3 normal = surface.normal;

    if (light.kind == SDF_LIGHT_DIRECTIONAL) {
        float lambert = sdfWrapDiffuse(dot(normal, light.position), surface.material.wrap);
        float occlusion = sdfLightVisibility(light.index, surface.shadowVisibility, surface.ambientOcclusion);

        response.diffuse = (light.color * ((light.weight * lambert) * occlusion));
        response.specular = (light.color * sdfMaterialSpecular(surface.material, normal, -surface.rayDirection, light.position, ((SDF_PI * light.weight) * occlusion)));
    } else if (light.kind == SDF_LIGHT_POINT) {
        float3 pointDirection;
        float reach = (light.weight * sdfPointReach(light.record, light.position, surface.position, pointDirection));

        if ((reach > 0.0) && surface.probed && (light.record.Shadows != 0u) && (light.index >= 0)) {
            reach *= sdfMeshLightVisibility(surface.probes, (uint)light.index);
        }
        if (reach > 0.0) {
            float pointLambert = sdfWrapDiffuse(dot(normal, pointDirection), surface.material.wrap);

            response.diffuse = (light.color * ((reach * pointLambert) * surface.ambientOcclusion));
            response.specular = (light.color * sdfMaterialSpecular(surface.material, normal, -surface.rayDirection, pointDirection, ((SDF_PI * reach) * surface.ambientOcclusion)));
        }
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
        float distanceSquared = dot(toLight, toLight);
        float3 lightDirection = (toLight * rsqrt(max(distanceSquared, 1.0e-8)));
        float facing = (max(dot(normal, lightDirection), 0.0) * saturate(dot(light.facing, -lightDirection)));

        response.diffuse = (light.color * ((light.weight * facing) / (distanceSquared + light.weight)));
    }

    return response;
}

#endif
#endif
