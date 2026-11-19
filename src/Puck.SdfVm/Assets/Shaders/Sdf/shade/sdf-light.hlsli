// The one light interface: every light the light stage walks, a light of the lights table (sdfLights) the view shades or
// a bound screen's area light, is one SdfLightSource, and sdfLightResponse answers what it adds at a shaded surface. The
// stage folds the responses in the lights' order: the diffuse terms into the radiance the material shade lights by, the
// rim and specular terms after the shade, and the attenuations into one factor on the reflected light. Every term is
// photometric (SdfPhotometry): a diffuse term is the illuminance over pi a white surface would show, in nits, and a
// specular term the luminance its lobe reflects. The glass pass walks the same lights for its highlights and the light
// its medium and its revealed flow surface receive.
#ifndef SHADE_SDF_LIGHT_HLSLI
#define SHADE_SDF_LIGHT_HLSLI
#include "Shared/luma.hlsli"
#include "sdf-mesh-irradiance.hlsli"
#include "sdf-toon.hlsli"

// The local adaptation's gain on a lit surface (SdfExposure.Local): the surface's exposure moves that share of the way
// from the view's toward the one its own illumination would meter, `incoming` (the luminance a white surface lit by the
// light arriving here would show) against the luminance the view's image meter reads (meterLuminance,
// SdfViewExposures.MeteredOf) taken as a scene of SDF_LOCAL_REFLECTANCE albedo,
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
// one probe at the camera that the volume reads there the moment a view arrives (SdfMeshIrradiance.TrySampleCamera): its
// diffuse light is the sample's six-axis light (meterLight, SdfViewExposures.LightOf: the radiance and sky-light cubes
// summed, the sky integrated along each open ray as a probe's is) read about the normal as a probe's cube is, and its
// reflection the environment over the share of the sky open about the camera (airLight) and the sample's mean
// luminance over the rest; its traced lights go unoccluded as with the volume off. It reads neither darkness nor the
// unoccluded sky. Before the sample's first reading the surface reads the open sky. Pixel passes only: a probe's relight
// never reads this, so an unmeasured hit never feeds the volume a guess.
bool sdfMeshAnswers(SdfMeshProbes probes) {
    return (probes.share > 0.0);
}
// The camera sample's light from the surroundings it did not see open: the mean luminance of its six axes over the
// closed share, grey, which its reflection reads; zero before the sample's first reading.
float3 sdfMeshCameraSurroundings() {
    float luminance = 0.0;

    [unroll] for (uint axis = 0u; (axis < 6u); axis++) {
        luminance += dot(passGroup.meterLight[axis].rgb, PuckRec709Luminance);
    }
    return ((passGroup.meterLight[0].w > 0.0) ? ((luminance / 6.0) * (1.0 - saturate(passGroup.airLight))) : 0.0).xxx;
}
// The light the view's camera sample receives about a normal, its six faces read as a probe's cube; false before the
// sample's first reading (meterLight's W zero).
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
// or before the sample has read the open sky under the volume's sky source.
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
// (sdfToonStyle), its ambient occlusion (a wrapped material's already relaxed toward 1), each shadow role's cascade
// visibility and coverage (the K row's lanes, sdfShadowVisibility) and which of them the surface pass queried, whether the
// view's cascades drew it, and the irradiance volume's probes about it, which occlude a traced point or spot light, and a
// shadowing directional wherever no cascade holds the point (probed is false while the volume is off, and every such
// light goes unoccluded).
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

// The probes' answer for a light at a surface: the share of the probes about it that see the light (sdfMeshLightVisibility)
// when the volume traced the light there (`traced`), otherwise one, times the surface's ambient occlusion, which stands in
// for the contact-scale shadowing the probes' spacing cannot resolve. A probe not yet measured answers lit, never dark.
// `known` is that share where the caller has already read it, negative where not, so a light reads the probes once.
float sdfLightProbeAnswer(SdfShadeSurface surface, int lightIndex, bool traced, float known) {
    return ((traced ? ((known >= 0.0) ? known : sdfMeshLightVisibility(surface.probes, (uint)lightIndex)) : 1.0) * surface.ambientOcclusion);
}
// One shadow role's answer at a surface: its cascades' visibility where they hold the point, the probes' answer where they
// do not, and across the band at the cascades' edge the two blended by the cascades' coverage (the K row's lanes,
// sdfShadowVisibility). A role whose lanes the surface pass did not query (a side the light does not reach) answers zero
// where `surface.shadowMeasured` says so. A slot that holds no role, and a view with no cascade, take the probes' answer
// whole, so every receiver asks the same question whichever view or pass it is in.
float sdfShadowRoleAnswer(SdfShadeSurface surface, int role, int lightIndex, bool traced, float known) {
    if ((role < 0) || !surface.cascaded) {
        return sdfLightProbeAnswer(surface, lightIndex, traced, known);
    }
    if (sdfComponent(surface.shadowMeasured, (uint)role) == 0.0) {
        return 0.0;
    }

    float cascade = sdfComponent(surface.shadowVisibility, (uint)role);
    float coverage = sdfComponent(surface.shadowVisibility, ((uint)role + SDF_SHADOW_ROLES));

    return ((coverage >= 1.0) ? cascade : lerp(sdfLightProbeAnswer(surface, lightIndex, traced, known), cascade, coverage));
}
// A directional light's shadow visibility. A slotted light takes the answer of the role its slot holds
// (sdfShadowRoleAnswer), so the cascades shadow a light only through the role that draws them. A handoff fades a stable
// slot from its outgoing light to its incoming one: the outgoing light keeps the slot's answer, which its cascades still
// render, easing toward ambient occlusion (the unslotted answer) with the handoff's weight, and the incoming light, which
// no cascade renders until it holds the slot, takes the probes' answer. An unslotted directional takes ambient occlusion.
float sdfLightVisibility(SdfShadeSurface surface, int lightIndex, bool traced, float known) {
    if (lightIndex < 0) {
        return surface.ambientOcclusion;
    }
    [loop]
    for (uint fade = 0u; fade < (uint)SDF_SHADOW_FADE_CAPACITY; fade++) {
        if (fade >= worldShadowHandoffCount()) { break; }
        SdfShadowHandoff handoff = sdfShadowHandoffs[fade];
        if (handoff.Slot < 0 || (uint)handoff.Slot >= SDF_MAX_SHADOW_SLOTS) { continue; }
        if (lightIndex == handoff.Outgoing) {
            return lerp(sdfShadowRoleAnswer(surface, worldShadowRoleOfSlot(handoff.Slot), lightIndex, traced, known), surface.ambientOcclusion, handoff.Weight);
        }
        if (lightIndex == handoff.Incoming) {
            return sdfLightProbeAnswer(surface, lightIndex, traced, known);
        }
    }
    [loop]
    for (uint shadowSlot = 0u; shadowSlot < SDF_MAX_SHADOW_SLOTS; shadowSlot++) {
        if (shadowSlot >= worldShadowStableCount()) { break; }
        if (lightIndex == sdfComponent(passGroup.shadowSlots, shadowSlot)) {
            return sdfShadowRoleAnswer(surface, worldShadowRoleOfSlot((int)shadowSlot), lightIndex, traced, known);
        }
    }
    return surface.ambientOcclusion;
}

// What one light adds at a surface: its diffuse term, its specular lobe and its rim brighten, and the factor it scales
// reflected light by (1 unless it occludes).
struct SdfLightResponse {
    float3 diffuse;
    float3 specular;
    float3 rim;
    float attenuation;
};

#ifdef SDF_SCREEN_SOURCES
// How many screen slots can light a surface this frame: one past the highest bound screen, within what every screen table
// holds; zero while screen lights are off.
uint sdfScreenLightCount() {
    if (worldScreenLightsDisabled()) {
        return 0u;
    }

    uint surfaceRows;
    uint mappingRows;
    uint emissionRows;
    uint stride;
    screenSurfaces.GetDimensions(surfaceRows, stride);
    screenMappings.GetDimensions(mappingRows, stride);
    sdfScreenLights.GetDimensions(emissionRows, stride);

    return min(min(screenLightLoopBound(), SDF_MAX_SCREEN_SURFACES),
        min((surfaceRows / WorldScreenSurfaceRows), min((mappingRows / WorldScreenMappingRows), emissionRows)));
}
#endif

// The lights a shading walk visits, in order: the lights table's, by the set bits of the light mask the walk starts with,
// then each bound screen slot by the set bits of its screen mask (one bit a slot, below sdfScreenLightCount). The masks
// are the view's (the pass block's lightMask, SdfLightSelection, and every screen) or subsets of them no wider than a
// workgroup (views narrows them to each tile's lights, sdfViewsTileLights), so every lane of a wave takes the same steps,
// and a light a mask leaves out costs nothing: the walk never visits it. A mask may leave out only a light that adds
// nothing where the walk's lanes shade.
struct SdfLightMasks {
    uint4 lights;
    uint screens;
};
// The view's masks: every light the view shades and every bound screen.
SdfLightMasks sdfViewLightMasks() {
    SdfLightMasks masks;

    masks.lights = passGroup.lightMask;
    masks.screens = 0xFFFFFFFFu;

    return masks;
}
struct SdfLightWalk {
    uint4 mask;
    // The mask word the walk is reading and its bits still to visit; past the last word, the table is done.
    uint word;
    uint bits;
    // The screen slots still to visit.
    uint screens;
};
SdfLightWalk sdfLightWalkStart(SdfLightMasks masks) {
    SdfLightWalk walk;

    walk.mask = masks.lights;
    walk.word = 0u;
    walk.bits = sdfComponent(masks.lights, 0u);
    walk.screens = masks.screens;

    return walk;
}
// A walk over the view's masks.
SdfLightWalk sdfLightWalkStart() {
    return sdfLightWalkStart(sdfViewLightMasks());
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
            walk.bits = ((walk.word < words) ? sdfComponent(walk.mask, walk.word) : 0u);
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
    uint screens = sdfScreenLightCount();

    [loop]
    while (walk.screens != 0u) {
        uint screenIndex = firstbitlow(walk.screens);

        walk.screens &= (walk.screens - 1u);
        if (screenIndex >= screens) {
            walk.screens = 0u;
            break;
        }
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
// - a directional or a point or spot light its wrapped Lambert term and its own GGX lobe, scaled by its reach (one for a
//   directional; the windowed inverse square and the spot's cone for a point, sdfPointReach) and its visibility
//   (sdfLightInputs);
// - a rim its view-dependent silhouette brighten;
// - an occluder a Gaussian dimming of reflected light, facing the surface;
// - a screen its glow toward what sits in front of its face: a Lambertian face of luminance L and area A casts
//   E = L * A * cos / (d^2 + A / pi) on a surface facing it, exact on the axis of a disc of that area.
// A specular lobe's scale is the light's illuminance, pi times its weight, since sdfMaterialSpecular answers the BRDF
// times the cosine. A wrap of 0 reduces each wrapped term to max(n.l, 0).
// The one evaluation of a light's direction, reach, probe share and visibility, which the default response and an
// authored light phase's stamp both read. `visibility` is the share of a directional or point light that reaches the
// surface. A directional's is its slot's role's answer (sdfLightVisibility): the cascades' measured shadow where they
// hold the point, which resolves every scale, and elsewhere (past the cascades, in a view with none, on glass) the
// probes' answer, so every receiver of one light asks the same question. A point or spot light takes the probes' answer:
// a light the irradiance volume traced (its Shadows flag, SdfLight.IsTraced; the probes record every shadowing directional
// too) takes the share of the probes about the surface that see it (sdfMeshLightVisibility), which resolves only the
// probes' spacing, times the surface's ambient occlusion; an untraced one takes the ambient occlusion alone. One for every
// other kind. The authored stamp requests every term; ordinary shading requests only the terms its light kind uses,
// preserving the no-program path's work.
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
    bool traced = surface.probed && light.record.Shadows != 0u && light.index >= 0;
    bool lit = (light.weight * inputs.reach > 0.0);
    // The probes' share read here, or negative where it was not, so a directional's visibility reads the probes once.
    float known = -1.0;
    if (traced && (authored || ((light.kind == SDF_LIGHT_POINT) && lit))) {
        inputs.probeShare = sdfMeshLightVisibility(surface.probes, (uint)light.index);
        known = inputs.probeShare;
    }
    if (light.kind == SDF_LIGHT_POINT) {
        inputs.visibility = inputs.probeShare * surface.ambientOcclusion;
    } else if (light.kind == SDF_LIGHT_DIRECTIONAL) {
        inputs.visibility = sdfLightVisibility(surface, light.index, (traced && lit), known);
    }
    return inputs;
}
SdfLightResponse sdfLightResponse(SdfLightSource light, SdfShadeSurface surface, SdfLightInputs inputs) {
    SdfLightResponse response;

    response.diffuse = float3(0.0, 0.0, 0.0);
    response.specular = float3(0.0, 0.0, 0.0);
    response.rim = float3(0.0, 0.0, 0.0);
    response.attenuation = 1.0;

    float3 normal = surface.normal;

    if ((light.kind == SDF_LIGHT_DIRECTIONAL) || (light.kind == SDF_LIGHT_POINT)) {
        float radiance = (light.weight * inputs.reach);
        float visibility = inputs.visibility;

        if ((radiance * visibility) > 0.0) {
            float lambert = sdfWrapDiffuse(dot(normal, inputs.direction), surface.material.wrap);

            if (sdfToonEnabled(surface.toon)) {
                response.diffuse = light.color * radiance * sdfToonBand(surface.toon, lambert, visibility);
                response.specular = light.color * sdfToonSpecular(surface.toon, surface.material, normal, -surface.rayDirection, inputs.direction, radiance, visibility);
            } else {
                response.diffuse = (light.color * ((radiance * lambert) * visibility));
                response.specular = (light.color * sdfMaterialSpecular(surface.material, normal, -surface.rayDirection, inputs.direction, ((SDF_PI * radiance) * visibility)));
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
