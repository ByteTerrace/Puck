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
#include "../frame/sdf-lights.hlsli"
#include "../frame/sdf-work.hlsli"
#include "sdf-mesh-irradiance.hlsli"
#include "sdf-toon.hlsli"

// The share of the probes about a surface that see a traced light (sdfMeshLightVisibility). The views pass reads it
// from its pixel's probe slot (sdfViewsLightVisibility, sdf-views-probes.hlsli), which the probe lookup resolved from the
// corners.
#if defined(SDF_VIEWS_PASS) && !defined(SDF_VIEWS_PROBES_PASS) && !defined(SDF_VIEWS_FILTER_PASS)
float sdfViewsLightVisibility(SdfMeshProbes probes, uint lightIndex);
#define sdfLightProbeVisibility sdfViewsLightVisibility
#else
#define sdfLightProbeVisibility sdfMeshLightVisibility
#endif

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
// unoccluded sky. After a cut the sample's light holds while the cut's own share is unknown (airLight zero), so the
// reflection reads the held surroundings alone. Before the sample's first reading the surface reads the open sky, its
// diffuse light and its reflection alike. Pixel passes only: a probe's relight never reads this, so an unmeasured hit
// never feeds the volume a guess.
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
// The indirect light at a surface the volume has measured nothing about: the camera sample's light, or before the sample
// has read the open sky under the volume's sky source.
float3 sdfMeshIndirectUnanswered(float3 normal) {
    float3 light;

    if (sdfMeshCameraLight(normal, light)) {
        return light;
    }
    if ((passGroup.indirectSources & SDF_INDIRECT_SOURCES_SKY) != 0u) {
        return (sdfSkyPhysicalIrradiance(normal) * (passGroup.indirectSourceGains.w / 3.141592653589793));
    }
    return float3(0.0, 0.0, 0.0);
}
// The volume's indirect light at a surface (sdfMeshIndirect), or where it has measured nothing the unanswered light.
float3 sdfMeshIndirectSeen(SdfMeshProbes probes, float3 normal) {
    if (sdfMeshAnswers(probes)) {
        return sdfMeshIndirect(probes, normal);
    }
    return sdfMeshIndirectUnanswered(normal);
}
// The environment a surface the volume has measured nothing about reflects: the environment as far as the camera sample
// sees the sky open, and its surroundings for the rest; the environment whole before the sample's first reading, as the
// diffuse light reads the open sky then (sdfMeshIndirectUnanswered).
float3 sdfMeshReflectionUnanswered(float3 direction, float3 environment) {
    if (passGroup.meterLight[0].w <= 0.0) {
        return environment;
    }
    return ((environment * saturate(passGroup.airLight)) + sdfMeshCameraSurroundings());
}
// The environment a surface reflects along a mirror direction under the volume (sdfMeshReflection), or where it has
// measured nothing the unanswered environment.
float3 sdfMeshReflectionSeen(SdfMeshProbes probes, float3 direction, float3 environment) {
    return (sdfMeshAnswers(probes)
        ? sdfMeshReflection(probes, direction, environment)
        : sdfMeshReflectionUnanswered(direction, environment));
}

// A bound screen's area light, outside the generated SDF_LIGHT_* kinds, which name the lights table's.
static const uint SdfLightScreen = 0x100u;

struct SdfLightSource {
    uint kind;
    float3 color;
    // A directional's illuminance over pi, a point's intensity over pi, a rim's luminance, an occluder's attenuation, a
    // screen's face area over pi.
    float weight;
    // A directional's unit direction toward the light; a point's, an occluder's or a screen's world position; a line's
    // midpoint.
    float3 position;
    // A rim's exponent, a point's or a screen's range, or an occluder's radius.
    float param;
    // A screen's face normal.
    float3 facing;
    // The table index used to find this light's own visibility; screens have no shadow slot.
    int index;
    // The table record, for a point's falloff and cone.
    SdfLight record;
};

// The surface a light answers at: its point, lit normal and camera ray, its material, its ambient occlusion (a wrapped material's already relaxed toward 1), each shadow role's cascade
// visibility and coverage (the K row's lanes, sdfShadowVisibility) and which of them the surface pass queried, whether the
// view's cascades drew it, and the irradiance volume's probes about it, which occlude a traced point or spot light, and a
// shadowing directional wherever no cascade holds the point (probed is false while the volume is off, and every such
// light goes unoccluded).
struct SdfShadeSurface {
    float3 position;
    float3 normal;
    float3 rayDirection;
    SdfMaterialData material;
    float ambientOcclusion;
    float4 shadowVisibility;
    // The primary role's transmitted colour beside its visibility (sdfMeshShadow's `transmitted`, filtered as the K row
    // is): white unless coloured glass stands between the surface and the key light.
    float3 shadowTint;
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
    return ((traced ? ((known >= 0.0) ? known : sdfLightProbeVisibility(surface.probes, (uint)lightIndex)) : 1.0) * surface.ambientOcclusion);
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
// How a directional light is shadowed: by the role its slot holds (`role`, -1 for a slot that holds none), faded toward
// the unslotted answer by `weight` while a handoff moves its slot to another light; by the probes alone while a handoff
// moves a slot to it; or, unslotted, by ambient occlusion alone.
static const uint SdfLightShadowedUnslotted = 0u;
static const uint SdfLightShadowedIncoming = 1u;
static const uint SdfLightShadowedSlotted = 2u;
struct SdfLightShadowing {
    uint kind;
    int role;
    float weight;
};
SdfLightShadowing sdfLightShadowingOf(int lightIndex) {
    SdfLightShadowing shadowing;

    shadowing.kind = SdfLightShadowedUnslotted;
    shadowing.role = -1;
    shadowing.weight = 0.0;
    if (lightIndex < 0) {
        return shadowing;
    }
    [loop]
    for (uint fade = 0u; fade < (uint)SDF_SHADOW_FADE_CAPACITY; fade++) {
        if (fade >= worldShadowHandoffCount()) { break; }
        SdfShadowHandoff handoff = sdfShadowHandoffs[fade];
        if (handoff.Slot < 0 || (uint)handoff.Slot >= SDF_MAX_SHADOW_SLOTS) { continue; }
        if (lightIndex == handoff.Outgoing) {
            shadowing.kind = SdfLightShadowedSlotted;
            shadowing.role = worldShadowRoleOfSlot(handoff.Slot);
            shadowing.weight = handoff.Weight;
            return shadowing;
        }
        if (lightIndex == handoff.Incoming) {
            shadowing.kind = SdfLightShadowedIncoming;
            return shadowing;
        }
    }
    [loop]
    for (uint shadowSlot = 0u; shadowSlot < SDF_MAX_SHADOW_SLOTS; shadowSlot++) {
        if (shadowSlot >= worldShadowStableCount()) { break; }
        if (lightIndex == sdfComponent(passGroup.shadowSlots, shadowSlot)) {
            shadowing.kind = SdfLightShadowedSlotted;
            shadowing.role = worldShadowRoleOfSlot((int)shadowSlot);
            return shadowing;
        }
    }
    return shadowing;
}
// A directional light's shadow visibility. A slotted light takes the answer of the role its slot holds
// (sdfShadowRoleAnswer), so the cascades shadow a light only through the role that draws them. A handoff fades a stable
// slot from its outgoing light to its incoming one: the outgoing light keeps the slot's answer, which its cascades still
// render, easing toward ambient occlusion (the unslotted answer) with the handoff's weight, and the incoming light, which
// no cascade renders until it holds the slot, takes the probes' answer. An unslotted directional takes ambient occlusion.
float sdfLightVisibility(SdfShadeSurface surface, SdfLightShadowing shadowing, int lightIndex, bool traced, float known) {
    if (shadowing.kind == SdfLightShadowedIncoming) {
        return sdfLightProbeAnswer(surface, lightIndex, traced, known);
    }
    if (shadowing.kind == SdfLightShadowedSlotted) {
        float answer = sdfShadowRoleAnswer(surface, shadowing.role, lightIndex, traced, known);

        return ((shadowing.weight > 0.0) ? lerp(answer, surface.ambientOcclusion, shadowing.weight) : answer);
    }
    return surface.ambientOcclusion;
}
float sdfLightVisibility(SdfShadeSurface surface, int lightIndex, bool traced, float known) {
    return sdfLightVisibility(surface, sdfLightShadowingOf(lightIndex), lightIndex, traced, known);
}
// The colour a directional light's light takes on its way to a surface, which its colour is multiplied by beside its
// visibility (sdfLightVisibility): the primary role's transmitted colour (the surface's shadowTint) where the cascades
// hold the point, blended to white across the band at their edge by their coverage as the visibility is, and faded to
// white with a handoff's weight; white for every other light, role and receiver, and wherever the role was not queried,
// whose visibility is zero.
float3 sdfLightTransmission(SdfShadeSurface surface, SdfLightShadowing shadowing) {
    if ((shadowing.kind != SdfLightShadowedSlotted) || (shadowing.role != 0) || !surface.cascaded) {
        return float3(1.0, 1.0, 1.0);
    }

    float coverage = saturate(sdfComponent(surface.shadowVisibility, SDF_SHADOW_ROLES));

    return lerp(float3(1.0, 1.0, 1.0), surface.shadowTint, (coverage * (1.0 - shadowing.weight)));
}

// What one light adds at a surface: its diffuse term, its specular lobe and its rim brighten, and the factor it scales
// reflected light by (1 unless it occludes).
struct SdfLightResponse {
    float3 diffuse;
    float3 specular;
    float3 rim;
    float attenuation;
    // The line an authored light phase raises, in [0, 1]: the walk keeps the largest, which the compose and ink phases
    // read (compose.line, ink.line). The default response raises none.
    float linework;
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
// then each bound screen slot by the set bits of its screen mask (one bit a slot in four words, below sdfScreenLightCount). The masks
// are the view's (the pass block's lightMask, SdfLightSelection, and every screen) or subsets of them no wider than a
// workgroup (views narrows them to each tile's lights, sdfViewsTileLights), so every lane of a wave takes the same steps,
// and a light a mask leaves out costs nothing: the walk never visits it. A mask may leave out only a light that adds
// nothing where the walk's lanes shade.
struct SdfLightMasks {
    uint4 lights;
    uint4 screens;
};
// The view's masks: every light the view shades and every bound screen.
SdfLightMasks sdfViewLightMasks() {
    SdfLightMasks masks;

    masks.lights = passGroup.lightMask;
    masks.screens = uint4(0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu);

    return masks;
}
// The view's masks narrowed to the lights that can reach a point any active lane of the wave shades, `position` this
// lane's: the box about the active lanes' points, and every light of the view's mask that sdfLightReachesBox keeps over
// it (a directional or a rim reaches everything and is kept), its lights shared out among the active lanes and their
// answers joined across the wave, with no group memory and no barrier. Every active lane must call it, each with the
// point its walk shades, and walk the masks it answers; the screens stay the view's. A light it leaves out adds nothing
// at any of those points, on the rule a views tile leaves one out by (sdfViewsTileLights). Views' tiles bound their
// walks as tightly already (a wave's box narrowed a hall's visits by under one in a hundred), so glass alone reads it.
SdfLightMasks sdfWaveLightMasks(float3 position) {
    SdfLightMasks masks = sdfViewLightMasks();
    float3 low = WaveActiveMin(position);
    float3 high = WaveActiveMax(position);
    uint rank = WavePrefixCountBits(true);
    uint active = WaveActiveCountBits(true);
    uint count = worldLightCount();
    uint4 kept = uint4(0u, 0u, 0u, 0u);

    [loop]
    for (uint first = 0u; (first < count); first += active) {
        uint index = (first + rank);
        uint4 bit = uint4(0u, 0u, 0u, 0u);

        if (index < count) {
            uint word = (index / 32u);
            uint flag = (1u << (index % 32u));

            if ((sdfComponent(masks.lights, word) & flag) != 0u) {
                SdfLight light = sdfLights[index];

                if (!sdfLightPositional(light.Kind)
                    || sdfLightReachesBox(light, worldPointLightPosition(light), low, high)) {
                    bit = uint4(((word == 0u) ? flag : 0u), ((word == 1u) ? flag : 0u), ((word == 2u) ? flag : 0u), ((word == 3u) ? flag : 0u));
                }
            }
        }
        kept |= WaveActiveBitOr(bit);
    }
    masks.lights = kept;

    return masks;
}
struct SdfLightWalk {
    uint4 mask;
    // The mask word the walk is reading and its bits still to visit; past the last word, the table is done.
    uint word;
    uint bits;
    // The screen mask, the screen word the walk is reading and its bits still to visit; past the last word, the screens
    // are done.
    uint4 screens;
    uint screenWord;
    uint screenBits;
};
SdfLightWalk sdfLightWalkStart(SdfLightMasks masks) {
    SdfLightWalk walk;

    walk.mask = masks.lights;
    walk.word = 0u;
    walk.bits = sdfComponent(masks.lights, 0u);
    walk.screens = masks.screens;
    walk.screenWord = 0u;
    walk.screenBits = masks.screens.x;

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
        light.position = (sdfLightPositional(record.Kind)
            ? worldPointLightPosition(record)
            : record.Direction);
        light.param = record.Param;
        light.index = (int)index;
        light.record = record;
        sdfWorkLights++;

        return true;
    }

#ifdef SDF_SCREEN_SOURCES
    uint screens = sdfScreenLightCount();
    uint screenWords = ((screens + 31u) / 32u);

    [loop]
    while (walk.screenWord < screenWords) {
        if (walk.screenBits == 0u) {
            walk.screenWord++;
            walk.screenBits = ((walk.screenWord < screenWords) ? sdfComponent(walk.screens, walk.screenWord) : 0u);
            continue;
        }

        uint screenIndex = ((walk.screenWord * 32u) + firstbitlow(walk.screenBits));

        walk.screenBits &= (walk.screenBits - 1u);
        if (screenIndex >= screens) {
            walk.screenWord = screenWords;
            break;
        }
        // A slot lights while it shows a bound source or is an emitter face (SdfScreenFace.Emitter, the state row's face),
        // whose light its material's reduction gives.
        if (!screenSourceBound(screenIndex) && !sdfScreenIsEmitter(worldScreenMapping(screenIndex))) {
            continue;
        }

        // right and up are orthonormal (SdfScreenSurface); the normalize absorbs upload drift.
        ScreenSurfaceData surface = worldScreenSurface(screenIndex);

        light.kind = SdfLightScreen;
        // The face's mean luminance, in nits (sdf-screen-emission.comp.hlsl).
        light.color = sdfScreenLights[screenIndex].rgb;
        // The range the host sizes from the face's peak luminance (SdfWorldTables' screen reaches).
        light.param = surface.origin.w;
        light.weight = (((4.0 * surface.right.w) * surface.up.w) / SDF_PI);
        light.position = surface.origin.xyz;
        light.facing = normalize(cross(surface.right.xyz, surface.up.xyz));
        sdfWorkLights++;

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
//   E = L * A * cos / (d^2 + A / pi) on a surface facing it, exact on the axis of a disc of that area, eased to zero at
//   its range as a point light is (sdfLightWindow).
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
// other kind. The authored stamp requests every term its program can read: a point light's visibility whether or not it
// lights, and the probe share of every traced light only for a program that reads `light.probeShare` (`probeShare`,
// SdfShadeReads.ProbeShare), since a directional's visibility otherwise reads the probes only where no cascade holds the
// point. Ordinary shading requests only the terms its light kind uses, preserving the no-program path's work.
// A point, spot or screen light's reach also fades out where its light falls under the view's cutoff
// (sdfLightCutoffFade), so the walk drops what the view's exposure cannot show.
// `none` says the light adds nothing at the surface: a point, spot or screen light whose reach there is exactly zero
// (past its range or cone, behind a screen's face, or under the cutoff), or a traced point or spot light whose share of
// the probes about the surface is exactly zero. Such a light adds nothing under the default response and an authored
// light phase alike (the light phase's door answers no light before it runs a program), which is what lets a views tile
// leave out a light that reaches none of its points or that none of its pixels' probes see (sdfViewsTileLights). A
// fixture meant to light through what the volume measured is authored untraced.
// `specularDirection` and `specularScale` are what the default specular lobe reads: the light's direction and pi times
// its reach for every kind but a line, whose lobe comes from its representative point (sdfLineSpecular).
// `transmitted` is the colour the light takes on its way through coloured glass, which the light phase's door multiplies
// the light's colour by before either response reads it (sdfLightTransmission; white for every kind but a directional).
struct SdfLightInputs { float3 direction; float reach; float probeShare; float visibility; bool none; float3 specularDirection; float specularScale; float3 transmitted; };
// The share of display white under which a positional light's contribution leaves the shading walk: the share that sizes
// every light's range at the world's darkest exposure (SdfPhotometry.RangeThreshold, SdfPhotometry.RangeOf), taken at
// the view's own exposure. A light keeps its whole response where it gives a white surface facing it twice that share of
// the view's white, and fades linearly to nothing at the share, so a views tile that leaves out a light whose bound over
// its points stays under the share (sdfViewsTileLights) cuts nothing a pixel shows. `luminance` is the light's luminance
// on a white surface facing it, in nits. The local adaptation (sdfLocalAdaptationGain) can lift what the fade removed in
// the darkest pixels by at most its gain.
float sdfLightCutoffFade(float luminance) {
    return saturate(((luminance * passGroup.exposure) / SDF_LIGHT_RANGE_THRESHOLD) - 1.0);
}
// Whether the irradiance volume traces a light of the lights table at `index` for its occlusion (SdfLight.IsTraced): a
// point, spot or line light whose record asks for shadows, among the lights a probe's light bits hold.
bool sdfLightTraces(SdfLight record, uint index) {
    return (sdfLightRanged(record.Kind) && (record.Shadows != 0u) && (index < (32u * SDF_MESH_IRRADIANCE_LIGHT_WORDS)));
}
// A line light's specular lobe: the direction toward the segment's point nearest the mirrored ray (Karis 2013's
// representative point), and the scale its lobe takes in place of pi times the reach: the segment's whole intensity over
// that point's squared distance, under the line's window, times the energy normalizations that widen a GGX lobe by the
// tube's radius seen from there (the sphere's, (a / a')^2) and by the half angle the segment subtends (the line's,
// a / a'), a' = sqrt(a^2 + sin * (3a + sin) / (4 (v . h + 0.001))) for each, as Unreal Engine 4's area lights normalize
// theirs. `window` is the line's window at its nearest point under the view's cutoff fade, which the diffuse reach shares.
float sdfLineSpecular(SdfLight light, float3 midpoint, SdfShadeSurface surface, float window, out float3 direction) {
    float3 halfSegment = light.SpotAxis;
    float3 toStart = ((midpoint - halfSegment) - surface.position);
    float3 span = (2.0 * halfSegment);
    float3 mirrored = reflect(surface.rayDirection, surface.normal);
    float mirroredSpan = dot(mirrored, span);
    float along = saturate(((dot(mirrored, toStart) * mirroredSpan) - dot(toStart, span)) / max((dot(span, span) - (mirroredSpan * mirroredSpan)), 1.0e-6));
    float3 toPoint = (toStart + (span * along));
    float nearest = sdfLineNearest(light);
    float pointSquared = max(dot(toPoint, toPoint), (nearest * nearest));

    direction = (toPoint * rsqrt(pointSquared));

    float rough = sqrt(((surface.material.roughness * surface.material.roughness) + SdfRoughnessFloorSquared));
    float alpha2 = (rough * rough);
    float vDotH = saturate(dot(-surface.rayDirection, normalize(direction - surface.rayDirection)));
    float sphereSine = saturate(light.SpotOffset * rsqrt(pointSquared));
    float sphereAlpha2 = (alpha2 + (((0.25 * sphereSine) * ((3.0 * rough) + sphereSine)) / (vDotH + 0.001)));
    float3 toEnd = (toStart + span);
    float subtended = clamp((dot(toStart, toEnd) * rsqrt(max((dot(toStart, toStart) * dot(toEnd, toEnd)), 1.0e-12))), -1.0, 1.0);
    float lineTangent = sqrt((1.0001 - subtended) / (1.0 + subtended));
    float lineAlpha2 = (alpha2 + (((0.25 * lineTangent) * ((3.0 * rough) + lineTangent)) / (vDotH + 0.001)));
    float energy = ((alpha2 / sphereAlpha2) * sqrt(alpha2 / lineAlpha2));

    return ((((SDF_PI * length(span)) * window) / pointSquared) * energy);
}
SdfLightInputs sdfLightInputs(SdfLightSource light, SdfShadeSurface surface, bool authored, bool probeShare) {
    SdfLightInputs inputs;
    inputs.direction = light.position;
    inputs.reach = 1.0;
    inputs.probeShare = 1.0;
    inputs.visibility = 1.0;
    inputs.specularScale = 0.0;
    inputs.transmitted = float3(1.0, 1.0, 1.0);
    bool positional = (sdfLightRanged(light.kind) || (light.kind == SdfLightScreen));
    if (light.kind == SDF_LIGHT_POINT) {
        inputs.reach = sdfPointReach(light.record, light.position, surface.position, inputs.direction);
        inputs.reach *= sdfLightCutoffFade(light.weight * inputs.reach);
    } else if (light.kind == SDF_LIGHT_LINE) {
        float3 toNearest = sdfLineNearestOffset(light.record, light.position, surface.position);
        float window = sdfLightWindow(dot(toNearest, toNearest), light.param);
        float fade = 0.0;

        inputs.reach = sdfLineReach(light.record, light.position, surface.position, inputs.direction);
        fade = sdfLightCutoffFade(light.weight * inputs.reach);
        inputs.reach *= fade;
        if (inputs.reach > 0.0) {
            inputs.specularScale = (sdfLineSpecular(light.record, light.position, surface, (window * fade), inputs.specularDirection));
        }
    } else if (light.kind == SdfLightScreen || (authored && light.kind != SDF_LIGHT_DIRECTIONAL)) {
        float3 delta = light.position - surface.position;
        float distanceSquared = dot(delta, delta);
        inputs.direction = delta * rsqrt(max(distanceSquared, 1.0e-8));
        if (light.kind == SdfLightScreen) {
            inputs.reach = (saturate(dot(light.facing, -inputs.direction)) * sdfLightWindow(distanceSquared, light.param)) / (distanceSquared + light.weight);
            inputs.reach *= sdfLightCutoffFade(dot(light.color, PuckRec709Luminance) * light.weight * inputs.reach);
        }
    }
    bool traced = surface.probed && light.record.Shadows != 0u && light.index >= 0;
    bool lit = (light.weight * inputs.reach > 0.0);
    // The probes' share read here, or negative where it was not, so a directional's visibility reads the probes once. A
    // positional light that reaches nothing here reads no probe: it adds nothing whatever they say.
    float known = -1.0;
    if (traced && (positional ? lit : probeShare)) {
        inputs.probeShare = sdfLightProbeVisibility(surface.probes, (uint)light.index);
        known = inputs.probeShare;
    }
    inputs.none = ((positional && !lit) || (sdfLightRanged(light.kind) && (known == 0.0)));
    if (sdfLightRanged(light.kind)) {
        inputs.visibility = inputs.probeShare * surface.ambientOcclusion;
    } else if (light.kind == SDF_LIGHT_DIRECTIONAL) {
        SdfLightShadowing shadowing = sdfLightShadowingOf(light.index);

        inputs.visibility = sdfLightVisibility(surface, shadowing, light.index, (traced && lit), known);
        inputs.transmitted = sdfLightTransmission(surface, shadowing);
    }
    if (light.kind != SDF_LIGHT_LINE) {
        inputs.specularDirection = inputs.direction;
        inputs.specularScale = (SDF_PI * inputs.reach);
    }
    return inputs;
}
// The light outputs an authored light phase writes, as a mask of SdfShadeProgram's light outputs in order (diffuse, rim,
// specular, attenuation: sdfShadeAssigned), which the default response does not compute. A program that replaces a term
// pays for its own, as hand-written shading would, never also for the engine's (a cel band beside a GGX lobe it
// discards); a non-finite authored term then falls back to the neutral value, no light and an attenuation of one.
static const uint SdfLightReplacesDiffuse = 1u;
static const uint SdfLightReplacesRim = 2u;
static const uint SdfLightReplacesSpecular = 4u;
static const uint SdfLightReplacesAttenuation = 8u;
SdfLightResponse sdfLightResponse(SdfLightSource light, SdfShadeSurface surface, SdfLightInputs inputs, uint replaced) {
    SdfLightResponse response;

    response.diffuse = float3(0.0, 0.0, 0.0);
    response.specular = float3(0.0, 0.0, 0.0);
    response.rim = float3(0.0, 0.0, 0.0);
    response.attenuation = 1.0;
    response.linework = 0.0;

    float3 normal = surface.normal;

    if ((light.kind == SDF_LIGHT_DIRECTIONAL) || sdfLightRanged(light.kind)) {
        float radiance = (light.weight * inputs.reach);
        float visibility = inputs.visibility;

        if ((radiance * visibility) > 0.0) {
            if ((replaced & SdfLightReplacesDiffuse) == 0u) {
                float lambert = sdfWrapDiffuse(dot(normal, inputs.direction), surface.material.wrap);

                response.diffuse = (light.color * ((radiance * lambert) * visibility));
            }
            if ((replaced & SdfLightReplacesSpecular) == 0u) {
                response.specular = (light.color * sdfMaterialSpecular(surface.material, normal, -surface.rayDirection, inputs.specularDirection, ((light.weight * inputs.specularScale) * visibility)));
            }
        }
    } else if (light.kind == SDF_LIGHT_RIM) {
        if ((replaced & SdfLightReplacesRim) == 0u) {
            response.rim = ((light.weight * light.color) * pow((1.0 - saturate(dot(normal, -surface.rayDirection))), light.param));
        }
    } else if (light.kind == SDF_LIGHT_OCCLUDER) {
        if ((light.weight > 0.0) && ((replaced & SdfLightReplacesAttenuation) == 0u)) {
            float3 delta = (light.position - surface.position);
            float distanceSquared = dot(delta, delta);
            float facing = ((distanceSquared > 1.0e-12) ? saturate(dot(normal, (delta * rsqrt(distanceSquared)))) : 1.0);
            float radius = max(light.param, 1.0e-6);

            response.attenuation = (1.0 - saturate(((light.weight * exp((-distanceSquared / (radius * radius)))) * facing)));
        }
    } else if ((light.kind == SdfLightScreen) && ((replaced & SdfLightReplacesDiffuse) == 0u)) {
        response.diffuse = light.color * (light.weight * max(dot(normal, inputs.direction), 0.0) * inputs.reach);
    }

    return response;
}

#endif
