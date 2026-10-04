// The atmosphere (render.atmosphere): the air between the camera and what it sees, from the sky block's atmosphere lanes
// (SdfSky.PackAtmosphere). SdfAir is the CPU reference, which evaluates the same closed forms in the same order with the
// same guards. The fog and the haze are exponential-height media (level where their falloff is zero) and the medium is
// water below a level surface; a ray crosses the air above the surface and the water below it as two segments in order,
// so its transmittance is their exact product, and each kind's in-scatter weight is what it adds seen through
// everything nearer. Within the air the fog and the haze share the air's in-scatter by their optical depths. Views,
// the resolve and the composite read it through sdf-transport.hlsli; the composite alone evaluates each kind's
// in-scatter colour, and counts each kind it evaluates at a pixel in its atmosphere detail row.
#ifndef SHADE_SDF_ATMOSPHERE_HLSLI
#define SHADE_SDF_ATMOSPHERE_HLSLI

// The flag the fog's authored colour sets in AirFlags. KEEP IN SYNC with SdfAir.FogColorAuthored.
static const uint SdfAirFogColorAuthored = 1u;
// The air lights the block bakes. KEEP IN SYNC with SdfAir.MaxLights.
static const uint SdfAirMaxLights = 4u;
// Below this |x| a height medium's share takes its series, so a ray parallel to its base (x = 0) integrates to its
// length. KEEP IN SYNC with SdfAir.SeriesLimit.
static const float SdfAirSeriesLimit = 1.0e-3;
// The largest exponent a height medium's density is raised by. KEEP IN SYNC with SdfAir.ExponentLimit.
static const float SdfAirExponentLimit = 60.0;
// The x below which a descending ray folds its two exponentials into one. KEEP IN SYNC with SdfAir.SteepLimit.
static const float SdfAirSteepLimit = -20.0;

// What the atmosphere does along a ray from the eye to one ray distance (SdfAirSpan): the transmittance, and each kind's
// in-scatter weight, fog in x, haze in y, medium in z.
struct SdfAir {
    float transmittance;
    float3 weights;
};

// (1 - e^-x) / x, one at x = 0.
float sdfAirShare(float x) {
    precise float share = ((abs(x) < SdfAirSeriesLimit) ? (1.0 - (x * (0.5 - (x / 6.0)))) : ((1.0 - exp(-x)) / x));

    return share;
}
// The optical depth of an exponential-height medium between two ray distances (SdfAir.Depth).
float sdfAirDepth(float extinction, float baseHeight, float falloff, float originY, float directionY, float nearDistance, float farDistance) {
    precise float extent = (farDistance - nearDistance);

    if ((extinction <= 0.0) || (extent <= 0.0)) {
        return 0.0;
    }
    if (falloff <= 0.0) {
        precise float level = (extinction * extent);

        return level;
    }

    precise float rise = (-((originY + (nearDistance * directionY)) - baseHeight) / falloff);
    precise float x = ((directionY * extent) / falloff);
    precise float depth = ((x < SdfAirSteepLimit)
        ? (((extinction * extent) * exp(min((rise - x), SdfAirExponentLimit))) / -x)
        : (((extinction * extent) * exp(min(rise, SdfAirExponentLimit))) * sdfAirShare(x)));

    return depth;
}
// One segment of a ray, all air or all water, after everything nearer (SdfAir's Segment).
void sdfAirSegment(SdfSkyBlock sky, float originY, float directionY, float nearDistance, float farDistance, bool fog, bool water, inout SdfAir air) {
    if (farDistance <= nearDistance) {
        return;
    }
    if (water) {
        precise float through = exp(-(sky.MediumExtinction * (farDistance - nearDistance)));

        air.weights.z += (air.transmittance * (1.0 - through));
        air.transmittance *= through;

        return;
    }

    float fogDepth = (fog ? sdfAirDepth(sky.FogExtinction, sky.FogBase, sky.FogFalloff, originY, directionY, nearDistance, farDistance) : 0.0);
    float hazeDepth = sdfAirDepth(sky.HazeExtinction, sky.HazeBase, sky.HazeFalloff, originY, directionY, nearDistance, farDistance);
    precise float through = exp(-(fogDepth + hazeDepth));
    precise float scattered = (air.transmittance * (1.0 - through));

    if (hazeDepth <= 0.0) {
        air.weights.x += scattered;
    } else if (fogDepth <= 0.0) {
        air.weights.y += scattered;
    } else {
        precise float share = ((scattered * fogDepth) / (fogDepth + hazeDepth));

        air.weights.x += share;
        air.weights.y += (scattered - share);
    }

    air.transmittance *= through;
}
// The atmosphere along a ray from the eye at `origin` along the unit `direction` to the ray distance `distance`: the air
// above the medium's surface and the water below it, in the order the ray crosses them. `fog` includes the fog: a
// surface's ray, never the sky's.
SdfAir sdfAirAlong(float3 origin, float3 direction, float distance, bool fog) {
    SdfSkyBlock sky = sdfSky[0];
    SdfAir air;

    air.transmittance = 1.0;
    air.weights = float3(0.0, 0.0, 0.0);
    if (distance <= 0.0) {
        return air;
    }
    if (sky.MediumExtinction > 0.0) {
        bool below = ((origin.y < sky.MediumSurface) || ((origin.y == sky.MediumSurface) && (direction.y < 0.0)));
        float crossing = distance;

        if (direction.y != 0.0) {
            float at = ((sky.MediumSurface - origin.y) / direction.y);

            if ((at > 0.0) && (at < distance)) {
                crossing = at;
            }
        }
        sdfAirSegment(sky, origin.y, direction.y, 0.0, crossing, fog, below, air);
        sdfAirSegment(sky, origin.y, direction.y, crossing, distance, fog, !below, air);
    } else {
        sdfAirSegment(sky, origin.y, direction.y, 0.0, distance, fog, false, air);
    }

    return air;
}
// Whether the sky share of a pixel passes through any of the atmosphere: the haze and the medium lie before the sky, the
// fog does not.
bool sdfAirBeforeSky() {
    return ((sdfSky[0].HazeExtinction > 0.0) || (sdfSky[0].MediumExtinction > 0.0));
}
// The Henyey-Greenstein phase times 4 pi (SdfAir.Phase), one for an isotropic medium.
float sdfAirPhase(float cosine, float anisotropy) {
    float g2 = (anisotropy * anisotropy);
    float denominator = max(((1.0 + g2) - ((2.0 * anisotropy) * cosine)), 1.0e-6);

    return ((1.0 - g2) / (denominator * sqrt(denominator)));
}
// One of the sky block's air lights: the unit direction toward it and its radiance.
void sdfAirLight(SdfSkyBlock sky, uint index, out float3 direction, out float3 radiance) {
    if (index == 0u) {
        direction = sky.AirLight0Direction; radiance = sky.AirLight0Radiance;
    } else if (index == 1u) {
        direction = sky.AirLight1Direction; radiance = sky.AirLight1Radiance;
    } else if (index == 2u) {
        direction = sky.AirLight2Direction; radiance = sky.AirLight2Radiance;
    } else {
        direction = sky.AirLight3Direction; radiance = sky.AirLight3Radiance;
    }
}
// The light-casting bodies' light scattered toward the eye along `direction` by a phase of `anisotropy` (SdfAir.Bodies).
float3 sdfAirBodies(float3 direction, float anisotropy) {
    SdfSkyBlock sky = sdfSky[0];
    uint count = min(sky.AirLightCount, SdfAirMaxLights);
    float3 light = float3(0.0, 0.0, 0.0);

    [loop]
    for (uint index = 0u; (index < count); index++) {
        float3 toward;
        float3 radiance;

        sdfAirLight(sky, index, toward, radiance);
        light += (radiance * sdfAirPhase(dot(direction, toward), anisotropy));
    }

    return light;
}
// Whether the fog in-scatters the sky (the environment map), not an authored colour.
bool sdfAirFogReadsSky() {
    return ((sdfSky[0].AirFlags & SdfAirFogColorAuthored) == 0u);
}
#endif
