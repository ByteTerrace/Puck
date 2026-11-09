// The one light interface: every light the light stage walks, a light of the lights table (sdfLights) the view shades or
// a bound screen's area light, is one SdfLightSource, and sdfLightResponse answers what it adds at a shaded surface. The
// stage folds the responses in the lights' order: the diffuse terms into the radiance the material shade lights by, the
// rim and specular terms after the shade, and the attenuations into one factor on the reflected light. Every term is
// photometric (SdfPhotometry): a diffuse term is the illuminance over pi a white surface would show, in nits, and a
// specular term the luminance its lobe reflects. The glass pass walks the same lights for its highlights and the light
// its medium and its revealed flow surface receive.
#ifndef SHADE_SDF_LIGHT_HLSLI
#define SHADE_SDF_LIGHT_HLSLI
#include "sdf-mesh-irradiance.hlsli"
#include "sdf-toon.hlsli"

// The local adaptation's gain on a lit surface (SdfExposure.Local): the surface's exposure moves that share of the way
// from the view's toward the one its own illumination would meter, `incoming` (the luminance a white surface lit by the
// light arriving here would show) against the view's meter reading taken as a scene of SDF_LOCAL_REFLECTANCE albedo,
// within SDF_LOCAL_ADAPTATION_RANGE stops; one where adaptation is off or nothing arrives.
float sdfLocalAdaptationGain(float incoming) {
    if ((passGroup.localAdaptation <= 0.0) || (passGroup.meterLuminance <= 0.0) || (incoming <= 0.0)) {
        return 1.0;
    }

    float reference = (passGroup.meterLuminance / SDF_LOCAL_REFLECTANCE);

    return exp2(clamp(log2(reference / incoming), -SDF_LOCAL_ADAPTATION_RANGE, SDF_LOCAL_ADAPTATION_RANGE) * saturate(passGroup.localAdaptation));
}

// Whether the irradiance volume answers a surface: some published probe about it measured it (sdfMeshProbesAt). While
// the volume is on, a surface it has not measured (every surface before a level first publishes; past every published
// probe after a camera cut, until the moved windows land) is lit as the view's camera sample measured the surroundings,
// one probe at the camera that the exposure meter reads there the moment a view arrives: its diffuse light is the
// sample's six-axis light (meterLight, SdfViewExposures.LightOf: the radiance and sky-light cubes summed, the sky
// integrated along each open ray as a probe's is) read about the normal as a probe's cube is, and its reflection the
// environment over the share of the sky open about the camera (airLight) and the luminance the meter read
// (meterLuminance) over the rest; its traced lights go unoccluded as with the volume off. It reads neither darkness nor
// the unoccluded sky, and the image agrees with the exposure the meter set from the same light. Before the meter's
// first reading no sample exists and the surface reads the open sky. Pixel passes only: a probe's relight never reads
// this, so an unmeasured hit never feeds the volume a guess.
bool sdfMeshAnswers(SdfMeshProbes probes) {
    return (probes.share > 0.0);
}
// The camera sample's light from the surroundings it did not see open: the meter's luminance over the closed share,
// grey, which its reflection reads.
float3 sdfMeshCameraSurroundings() {
    return (max(passGroup.meterLuminance, 0.0) * (1.0 - saturate(passGroup.airLight))).xxx;
}
// The light the view's camera sample receives about a normal, its six faces read as a probe's cube; false before the
// meter's first reading (meterLight's W zero).
bool sdfMeshCameraLight(float3 normal, out float3 light) {
    light = float3(0.0, 0.0, 0.0);
    if (passGroup.meterLight[0].w <= 0.0) {
        return false;
    }

    float3 squared = (normal * normal);

    [unroll] for (uint axis = 0u; (axis < 3u); axis++) {
        light += (squared[axis] * passGroup.meterLight[sdfMeshCubeFace(normal, axis)].rgb);
    }
    return true;
}
// The volume's indirect light at a surface (sdfMeshIndirect), or where it has measured nothing the camera sample's light,
// or before the meter has read the open sky under the volume's sky source.
float3 sdfMeshIndirectSeen(SdfMeshProbes probes, float3 normal) {
    if (sdfMeshAnswers(probes)) {
        return sdfMeshIndirect(probes, normal);
    }

    float3 light;

    if (sdfMeshCameraLight(normal, light)) {
        return light;
    }
    if ((passGroup.indirectSources & SDF_INDIRECT_SOURCES_SKY) != 0u) {
        return (sdfSkyPhysicalIrradiance(normal) * (passGroup.indirectSourceGains.w / 3.141592653589793));
    }
    return float3(0.0, 0.0, 0.0);
}
// The environment a surface reflects along a mirror direction under the volume (sdfMeshReflection), or where it has
// measured nothing the camera sample's: the environment as far as it sees the sky open, and its surroundings for the
// rest.
float3 sdfMeshReflectionSeen(SdfMeshProbes probes, float3 direction, float3 environment) {
    return (sdfMeshAnswers(probes)
        ? sdfMeshReflection(probes, direction, environment)
        : ((environment * saturate(passGroup.airLight)) + sdfMeshCameraSurroundings()));
}

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

// The surface a light answers at: its point, lit normal and camera ray, its material and the cel style it shades with
// (sdfToonStyle), its ambient occlusion (a wrapped material's already relaxed toward 1), each stable slot's shadow
// visibility, whether the view's cascades drew it, and the irradiance volume's probes about it, which occlude a traced
// point or spot light, and a shadowing directional in a view with no cascade (probed is false while the volume is off,
// and every such light goes unoccluded).
struct SdfShadeSurface {
    float3 position;
    float3 normal;
    float3 rayDirection;
    SdfMaterialData material;
    SdfToonStyle toon;
    float ambientOcclusion;
    float4 shadowVisibility;
    float4 shadowMeasured;
    bool cascaded;
    bool probed;
    SdfMeshProbes probes;
};

// A light's shadow visibility. A handoff fades a stable slot from its outgoing light to its incoming one: the outgoing
// light's own occlusion deficit, which the slot's cascades still render, fades out with the handoff's weight, and the
// incoming light, which no cascade renders until it holds the slot, shades unshadowed. Unslotted directionals retain
// ambient occlusion.
float sdfLightVisibility(int lightIndex, float4 stable, float ambientOcclusion, float4 measured) {
    if (lightIndex < 0) {
        return ambientOcclusion;
    }
    [loop]
    for (uint fade = 0u; fade < (uint)SDF_SHADOW_FADE_CAPACITY; fade++) {
        if (fade >= worldShadowHandoffCount()) { break; }
        SdfShadowHandoff handoff = sdfShadowHandoffs[fade];
        if (handoff.Slot < 0 || (uint)handoff.Slot >= SDF_MAX_SHADOW_SLOTS) { continue; }
        if (lightIndex == handoff.Outgoing) {
            if (sdfComponent(measured, (uint)handoff.Slot) == 0.0) { return 0.0; }
            return (1.0 - ((1.0 - sdfComponent(stable, (uint)handoff.Slot)) * (1.0 - handoff.Weight)));
        }
        if (lightIndex == handoff.Incoming) {
            return sdfComponent(measured, (uint)handoff.Slot);
        }
    }
    [loop]
    for (uint shadowSlot = 0u; shadowSlot < SDF_MAX_SHADOW_SLOTS; shadowSlot++) {
        if (shadowSlot >= worldShadowStableCount()) { break; }
        if (lightIndex == sdfComponent(passGroup.shadowSlots, shadowSlot)) {
            return sdfComponent(stable, shadowSlot) * sdfComponent(measured, shadowSlot);
        }
    }
    return ambientOcclusion;
}
float sdfLightVisibility(int lightIndex, float4 stable, float ambientOcclusion) {
    return sdfLightVisibility(lightIndex, stable, ambientOcclusion, float4(1.0, 1.0, 1.0, 1.0));
}

// What one light adds at a surface: its diffuse term, its specular lobe and its rim brighten, and the factor it scales
// reflected light by (1 unless it occludes), each the continuous response's whatever the style; under a cel style, a
// directional or point light's step as well (sdfToonLight), which the stage applies once the walk is done
// (sdfToonShade).
struct SdfLightResponse {
    float3 diffuse;
    float3 specular;
    float3 rim;
    float attenuation;
    SdfToonLight toon;
};

// The lights a shading walk visits, in order: the lights table's the view shades, by the set bits of its light mask
// (SdfLightSelection), then while screen lights are on each screen slot up to the highest bound one. The mask is the
// pass block's, uniform over a dispatch, so every lane of a wave takes the same steps, and a light the view leaves out
// costs nothing: the walk never visits it.
struct SdfLightWalk {
    // The mask word the walk is reading and its bits still to visit; past the last word, the table is done.
    uint word;
    uint bits;
    // The next screen slot.
    uint screen;
};
SdfLightWalk sdfLightWalkStart() {
    SdfLightWalk walk;

    walk.word = 0u;
    walk.bits = sdfComponent(passGroup.lightMask, 0u);
    walk.screen = 0u;

    return walk;
}
// The walk's next light in `light`, or false once every light is visited.
bool sdfLightNext(inout SdfLightWalk walk, out SdfLightSource light) {
    light = (SdfLightSource)0;
    light.index = -1;

    uint tableCount = worldLightCount();
    uint words = ((tableCount + 31u) / 32u);

    [loop]
    while (walk.word < words) {
        if (walk.bits == 0u) {
            walk.word++;
            walk.bits = ((walk.word < words) ? sdfComponent(passGroup.lightMask, walk.word) : 0u);
            continue;
        }

        uint index = ((walk.word * 32u) + firstbitlow(walk.bits));

        walk.bits &= (walk.bits - 1u);
        if (index >= tableCount) {
            walk.word = words;
            break;
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
    if (worldScreenLightsDisabled()) {
        return false;
    }

    uint surfaceRows;
    uint mappingRows;
    uint emissionRows;
    uint stride;
    screenSurfaces.GetDimensions(surfaceRows, stride);
    screenMappings.GetDimensions(mappingRows, stride);
    sdfScreenLights.GetDimensions(emissionRows, stride);
    uint screens = min(min(screenLightLoopBound(), SDF_MAX_SCREEN_SURFACES),
        min((surfaceRows / WorldScreenSurfaceRows), min((mappingRows / WorldScreenMappingRows), emissionRows)));

    [loop]
    while (walk.screen < screens) {
        uint screenIndex = walk.screen;

        walk.screen++;
        if (!screenSourceBound(screenIndex)) {
            continue;
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
    }
#endif
    return false;
}
// What `light` adds at `surface`:
// - a directional its wrapped Lambert term and its own GGX lobe under its shadow visibility: its cascades', or in a view
//   with no cascade a shadowing directional's (its Shadows flag) share of the probes about the surface that see it
//   (sdfMeshLightVisibility), so a view without cascades never lights every face turned toward the sun;
// - a point or spot its wrapped Lambert term and its own GGX lobe under the windowed inverse square and the spot's cone
//   (sdfPointReach), ambient occlusion and, for a static light the irradiance volume traced (its Shadows flag,
//   SdfLight.IsTraced), the share of the probes about the surface that see it (sdfMeshLightVisibility);
// - a rim its view-dependent silhouette brighten;
// - an occluder a Gaussian dimming of reflected light, facing the surface;
// - a screen its glow toward what sits in front of its face: a Lambertian face of luminance L and area A casts
//   E = L * A * cos / (d^2 + A / pi) on a surface facing it, exact on the axis of a disc of that area.
// A specular lobe's scale is the light's illuminance, pi times its weight, since sdfMaterialSpecular answers the BRDF
// times the cosine. A wrap of 0 reduces each wrapped term to max(n.l, 0).
// The one evaluation of a light's direction, reach, probe share and visibility, which the default response and an
// authored light phase's stamp both read. `visibility` is the share of the light the default response lets through:
// the cascade slot's (the probes' where no cascade measured it) for a directional, the probes' share for a traced point
// light, and one for every other kind. The authored stamp requests every term; ordinary shading requests only the
// terms its light kind uses, preserving the no-program path's work.
struct SdfLightInputs { float3 direction; float reach; float probeShare; float visibility; };
SdfLightInputs sdfLightInputs(SdfLightSource light, SdfShadeSurface surface, bool authored) {
    SdfLightInputs inputs;
    inputs.direction = light.position;
    inputs.reach = 1.0;
    inputs.probeShare = 1.0;
    inputs.visibility = 1.0;
    if (light.kind == SDF_LIGHT_POINT) {
        inputs.reach = sdfPointReach(light.record, light.position, surface.position, inputs.direction);
    } else if (light.kind == SdfLightScreen || (authored && light.kind != SDF_LIGHT_DIRECTIONAL)) {
        float3 delta = light.position - surface.position;
        float distanceSquared = dot(delta, delta);
        inputs.direction = delta * rsqrt(max(distanceSquared, 1.0e-8));
        if (light.kind == SdfLightScreen) inputs.reach = saturate(dot(light.facing, -inputs.direction)) / (distanceSquared + light.weight);
    }
    if (surface.probed && light.record.Shadows != 0u && light.index >= 0 &&
        (authored || (light.kind == SDF_LIGHT_DIRECTIONAL && !surface.cascaded) ||
         (light.kind == SDF_LIGHT_POINT && light.weight * inputs.reach > 0.0))) {
        inputs.probeShare = sdfMeshLightVisibility(surface.probes, (uint)light.index);
    }
    if (light.kind == SDF_LIGHT_DIRECTIONAL) {
        inputs.visibility = !surface.cascaded && surface.probed && light.record.Shadows != 0u && light.index >= 0
            ? inputs.probeShare : sdfLightVisibility(light.index, surface.shadowVisibility, surface.ambientOcclusion, surface.shadowMeasured);
    } else if (light.kind == SDF_LIGHT_POINT) {
        inputs.visibility = inputs.probeShare;
    }
    return inputs;
}
SdfLightResponse sdfLightResponse(SdfLightSource light, SdfShadeSurface surface, SdfLightInputs inputs) {
    SdfLightResponse response;

    response.diffuse = float3(0.0, 0.0, 0.0);
    response.specular = float3(0.0, 0.0, 0.0);
    response.rim = float3(0.0, 0.0, 0.0);
    response.attenuation = 1.0;
    response.toon = (SdfToonLight)0;

    float3 normal = surface.normal;
    bool toon = sdfToonEnabled(surface.toon);

    if (light.kind == SDF_LIGHT_DIRECTIONAL) {
        float lambert = sdfWrapDiffuse(dot(normal, light.position), surface.material.wrap);
        float occlusion = inputs.visibility;

        response.diffuse = (light.color * ((light.weight * lambert) * occlusion));
        response.specular = (light.color * sdfMaterialSpecular(surface.material, normal, -surface.rayDirection, light.position, ((SDF_PI * light.weight) * occlusion)));
        if (toon) {
            // The band reads the form: its normal, and its filtered shadow where the cascades measured one (the probes'
            // share stands in where no cascade drew the surface, as for the continuous response). The region is whether
            // the surface stands in the open air (the style's `open`, from the sky the probes see above it), so a form or
            // cast shadow outdoors steps and a room does not. The cascades cannot answer it: the inner face of a
            // building's outer wall is that wall's own exit from the light, so it would read as reached. Without the
            // volume the cascades' visibility stands in.
            float cosine = sdfWrapDiffuse(dot(surface.toon.normal, light.position), surface.material.wrap);
            bool probeShadowed = (!surface.cascaded && surface.probed && (light.record.Shadows != 0u) && (light.index >= 0));
            float visibility = (probeShadowed ? inputs.probeShare
                : sdfLightVisibility(light.index, surface.toon.shadows, surface.ambientOcclusion, surface.shadowMeasured));
            float band = sdfToonBand(surface.toon, cosine, visibility, sdfToonShadowWidth(surface.toon, light.index));
            float region = (surface.probed ? surface.toon.open : saturate(occlusion));
            response.toon = sdfToonLight(surface.toon, light.color * light.weight, cosine, band, region);
        }
    } else if (light.kind == SDF_LIGHT_POINT) {
        float3 pointDirection = inputs.direction;
        float reach = (light.weight * inputs.reach) * inputs.visibility;
        if (reach > 0.0) {
            float pointLambert = sdfWrapDiffuse(dot(normal, pointDirection), surface.material.wrap);

            response.diffuse = (light.color * ((reach * pointLambert) * surface.ambientOcclusion));
            response.specular = (light.color * sdfMaterialSpecular(surface.material, normal, -surface.rayDirection, pointDirection, ((SDF_PI * reach) * surface.ambientOcclusion)));
            if (toon) {
                // The reach already holds the light's traced visibility, so its step reaches wherever the light does.
                float cosine = sdfWrapDiffuse(dot(surface.toon.normal, pointDirection), surface.material.wrap);
                response.toon = sdfToonLight(surface.toon, light.color * (reach * surface.ambientOcclusion), cosine,
                    sdfToonStep(surface.toon, cosine), 1.0);
            }
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
        response.diffuse = light.color * (light.weight * max(dot(normal, inputs.direction), 0.0) * inputs.reach);
    }

    return response;
}
SdfLightResponse sdfLightResponse(SdfLightSource light, SdfShadeSurface surface) {
    return sdfLightResponse(light, surface, sdfLightInputs(light, surface, false));
}

#endif
