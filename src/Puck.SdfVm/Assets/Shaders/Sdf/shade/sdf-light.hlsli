// The one light interface: every light the light stage walks, an environment light (frame/sdf-lights.hlsli) or a bound
// screen's area light, is one SdfLight, and sdfLightResponse answers what it adds at a shaded surface. The stage folds the
// responses in the lights' order: the diffuse terms into the radiance the material shade lights by, the rim and specular
// terms after the shade, and the attenuations into one factor on the reflected light.
#ifndef SHADE_SDF_LIGHT_HLSLI
#define SHADE_SDF_LIGHT_HLSLI
#ifdef SDF_VIEWS_PASS

// A bound screen's area light, outside the generated SDF_LIGHT_* kinds, which name the environment's.
static const uint SdfLightScreen = 0x100u;

struct SdfLight {
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
    // Whether the light is the shadow light, whose Lambert term the key light's soft shadow scales.
    bool key;
};

// The surface a light answers at: its point, lit normal and camera ray, its material, its ambient occlusion (a wrapped
// material's already relaxed toward 1), the key light's shadow visibility, and the environment's scales.
struct SdfShadeSurface {
    float3 position;
    float3 normal;
    float3 rayDirection;
    SdfMaterialData material;
    float ambientOcclusion;
    float keyVisibility;
};

// What one light adds at a surface: its diffuse term, its specular lobe and its rim brighten, and the factor it scales
// reflected light by (1 unless it occludes).
struct SdfLightResponse {
    float3 diffuse;
    float3 specular;
    float3 rim;
    float attenuation;
};

// The lights the stage walks: the environment's, then one slot per screen up to the highest bound one while screen
// lights are on.
uint sdfLightCount() {
    uint count = (lightFrame[0].Count);
#ifdef SDF_SCREEN_SOURCES
    if (!worldScreenLightsDisabled()) {
        count += screenLightLoopBound();
    }
#endif
    return count;
}
// The light at `index` of the walk, or false for a screen slot whose source is not bound this frame.
bool sdfLightAt(uint index, out SdfLight light) {
    light = (SdfLight)0;

    uint environmentCount = (lightFrame[0].Count);

    if (index < environmentCount) {
        SdfLightData environment = lights[index];

        light.kind = environment.Kind;
        light.color = environment.Color;
        light.weight = environment.Weight;
        light.position = (((environment.Kind == SDF_LIGHT_POINT) || (environment.Kind == SDF_LIGHT_OCCLUDER))
            ? worldPointLightPosition(environment)
            : environment.Direction);
        light.param = environment.Parameter;
        light.key = ((int)index == (lightFrame[0].ShadowIndex));

        return true;
    }

#ifdef SDF_SCREEN_SOURCES
    uint screenIndex = (index - environmentCount);

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
// - a directional its wrapped Lambert term under the key light's visibility when it is the shadow light and under ambient
//   occlusion otherwise, scaled by the sun scale;
// - a hemisphere its floor plus its gradient along the normal's height under ambient occlusion, scaled by the ambient
//   scale;
// - a point its wrapped Lambert term and its own GGX lobe under an inverse-square falloff and ambient occlusion;
// - a rim its view-dependent silhouette brighten;
// - an occluder a Gaussian dimming of reflected light, facing the surface;
// - a screen its glow toward what sits in front of its face, under an inverse-square falloff.
// A wrap of 0 reduces each wrapped term to max(n.l, 0).
SdfLightResponse sdfLightResponse(SdfLight light, SdfShadeSurface surface) {
    SdfLightResponse response;

    response.diffuse = float3(0.0, 0.0, 0.0);
    response.specular = float3(0.0, 0.0, 0.0);
    response.rim = float3(0.0, 0.0, 0.0);
    response.attenuation = 1.0;

    float3 normal = surface.normal;

    if (light.kind == SDF_LIGHT_DIRECTIONAL) {
        float lambert = sdfWrapDiffuse(dot(normal, light.position), surface.material.wrap);
        float occlusion = (light.key ? surface.keyVisibility : surface.ambientOcclusion);

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
