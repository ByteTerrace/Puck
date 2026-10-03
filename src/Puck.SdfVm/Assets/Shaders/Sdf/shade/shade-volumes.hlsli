// Bounded flow/cloud volumes (Puck.SignedDistance.SdfVolume, a participating medium, never a distance-field shape):
// sdfVolumes, eleven float4 rows a volume, paired with SdfWorldTables.PackVolumes, which bakes each medium's motion from
// the frame's presented tick, so no pass reads a clock. The composite pass alone integrates them, after the surface and
// the sky, clipping the pixel's surface share at its surface and its sky share at the far distance.
#ifndef SDF_SHADE_VOLUMES_HLSLI
#define SDF_SHADE_VOLUMES_HLSLI
#include "../field/sdf-noise.hlsli"
#include "../field/sdf-quaternion.hlsli"
// The table's capacity in volumes. KEEP IN SYNC with SdfWorldTables.PackVolumes / SdfProgramBuilder.MaxVolumes.
static const uint SdfVolumeCount = 64u;
struct SdfVolumeData {
    float3 position;
    int dynamicSlot;
    float4 rotation;
    float3 halfExtent;
    float axis;
    float width;
    float advection;    // the host-baked drift in noise cells: along the column (a flow) or X (a cloud)
    uint seed;
    float steps;
    float intensity;
    float extinction;
    float pulse;        // the host-baked density gain at the pulse's phase
    int intensityLane;
    uint rampCount;
    uint kind;
    float advectionZ;   // a cloud's host-baked drift along Z, in noise cells
    float coverage;
    float softness;
    float4 ramp[4];
};
SdfVolumeData sdfLoadVolume(uint index) {
    uint b = index * 11u;
    SdfVolumeData v;
    float4 r0 = sdfVolumes[b];
    v.position = r0.xyz; v.dynamicSlot = (int)r0.w;
    v.rotation = sdfVolumes[b + 1u];
    float4 r2 = sdfVolumes[b + 2u];
    v.halfExtent = r2.xyz; v.axis = r2.w;
    float4 r3 = sdfVolumes[b + 3u];
    v.width = r3.x; v.advection = r3.y; v.seed = asuint(r3.z); v.steps = r3.w;
    float4 r4 = sdfVolumes[b + 4u];
    v.intensity = r4.x; v.extinction = r4.y; v.pulse = r4.z;
    float4 r5 = sdfVolumes[b + 5u];
    v.intensityLane = (int)r5.x; v.rampCount = (uint)r5.y; v.kind = (uint)r5.z; v.advectionZ = r5.w;
    v.coverage = sdfVolumes[b + 10u].x; v.softness = sdfVolumes[b + 10u].y;
    [unroll] for (uint i = 0u; i < 4u; i++) v.ramp[i] = sdfVolumes[b + 6u + i];
    return v;
}
// Transforms a world point/direction into the volume's own frame: first undo the riding dynamic slot's rigid
// transform (unless dynamicSlot is SDF_TRANSFORM_SLOT_NONE), matching how a dynamic shape instance resolves its
// bound (field/sdf-map.hlsli's rigidBasePosition), then undo the volume's own Position/Rotation offset within that
// frame. A direction only undoes the rotations (no translation).
float3 sdfVolumeLocalPoint(SdfVolumeData v, float3 worldPoint) {
    float3 p = worldPoint;

    if (v.dynamicSlot != SDF_TRANSFORM_SLOT_NONE) {
        uint slot = (uint)v.dynamicSlot;
        float4 dynamicPosition = sdfDynamicTransforms[(3u * slot)];
        float4 dynamicOrientation = sdfDynamicTransforms[((3u * slot) + 1u)];

        p = rotatePointByInverseQuaternion((p - dynamicPosition.xyz), dynamicOrientation);
    }

    return rotatePointByInverseQuaternion((p - v.position), v.rotation);
}
float3 sdfVolumeLocalDirection(SdfVolumeData v, float3 worldDirection) {
    float3 d = worldDirection;

    if (v.dynamicSlot != SDF_TRANSFORM_SLOT_NONE) {
        uint slot = (uint)v.dynamicSlot;
        float4 dynamicOrientation = sdfDynamicTransforms[((3u * slot) + 1u)];

        d = rotatePointByInverseQuaternion(d, dynamicOrientation);
    }

    return rotatePointByInverseQuaternion(d, v.rotation);
}
// An absent lane uses constant gain; a selected lane without a riding slot reads zero.
float sdfVolumeIntensityScale(SdfVolumeData v) {
    if (v.intensityLane < 0) return 1.0;
    if (v.dynamicSlot == SDF_TRANSFORM_SLOT_NONE) return 0.0;
    return max(sdfDynamicTransforms[3u * (uint)v.dynamicSlot + 2u][(uint)v.intensityLane], 0.0);
}

// The volume-local AABB slab test (Kay/Kajiya), centered on the origin with the given half-extent. localDirection
// need not be renormalized after the frame transforms above — both are pure rotations, so a unit world ray stays
// unit, and t stays in world-distance units on both sides.
float2 sdfVolumeSlabInterval(float3 localOrigin, float3 localDirection, float3 halfExtent) {
    float3 guardedDirection = float3(
        ((abs(localDirection.x) > 1.0e-8) ? localDirection.x : 1.0e-8),
        ((abs(localDirection.y) > 1.0e-8) ? localDirection.y : 1.0e-8),
        ((abs(localDirection.z) > 1.0e-8) ? localDirection.z : 1.0e-8)
    );
    float3 invDirection = (1.0 / guardedDirection);
    float3 t0 = ((-halfExtent - localOrigin) * invDirection);
    float3 t1 = ((halfExtent - localOrigin) * invDirection);
    float3 tMin = min(t0, t1);
    float3 tMax = max(t0, t1);

    return float2(max(tMin.x, max(tMin.y, tMin.z)), min(tMax.x, min(tMax.y, tMax.z)));
}

// Density selects color at each local sample, before emission/absorption integration.
float3 sdfVolumeRamp(SdfVolumeData v, float density) {
    float3 color = v.ramp[0].rgb;
    [loop] for (uint i = 1u; i < v.rampCount; i++) {
        float blend = saturate((density - v.ramp[i - 1u].w) / (v.ramp[i].w - v.ramp[i - 1u].w));
        color = lerp(color, v.ramp[i].rgb, blend);
    }
    return color;
}
// A true 3D medium, clipped smoothly inside its own bound. Width is the noise-cell size in world units;
// coverage moves a density threshold, not a screen-space alpha. The ramp and height tint provide art-directed
// illumination; this does not trace cloud shadows or multiple scattering. The octaves scale by whole numbers, so each
// stays periodic in the lattice's period and the host-reduced advection never shows a seam.
float sdfCloudDensity(SdfVolumeData v, float3 p) {
    float3 unit = p / v.halfExtent;
    float envelope = smoothstep(0.0, 0.3, 1.0 - dot(unit, unit));
    float3 q = ((p / v.width) + float3(v.advection, 0.0, v.advectionZ));
    float noise = 0.5 + 0.5 * (
        0.57 * sdfPeriodicNoise3(q, v.seed) +
        0.28 * sdfPeriodicNoise3(q * 2.0 + 7.1, v.seed + 19u) +
        0.15 * sdfPeriodicNoise3(q * 4.0 - 3.7, v.seed + 53u));
    return envelope * smoothstep(1.0 - v.coverage - v.softness, 1.0 - v.coverage + v.softness, noise);
}
void sdfIntegrateVolume(SdfVolumeData v, float3 localOrigin, float3 localDirection, float tBegin, float tEnd,
    float dither, out float3 radianceOut, out float transmissionOut) {
    int steps = (int)v.steps;
    float stepLength = (tEnd - tBegin) / (float)steps;
    float3 radiance = 0.0;
    float transmission = 1.0;
    [loop] for (int i = 0; i < steps; i++) {
        // Each sample is a march step, counted before a flow column's sample outside its axis skips the rest.
        sdfWorkSteps += 1u;
        float sampleT = tBegin + ((float)i + 0.5 + dither * 0.49) * stepLength;
        float3 p = localOrigin + localDirection * sampleT;
        float density;
        if (v.kind == 1u) {
            density = saturate(sdfCloudDensity(v, p) * v.pulse);
        } else {
            float axial = (v.halfExtent.y - p.y) / v.axis;
            if (axial < 0.0 || axial >= 1.0) continue;
            float taper = 1.0 - axial;
            float radius = length(p.xz) / max(v.width * taper, 1.0e-6);
            float3 flow = ((p / v.width) + float3(0.0, v.advection, 0.0));
            float noise = 0.5 + 0.5 * sdfPeriodicNoise3(flow, v.seed);
            density = saturate(exp(-radius * radius) * taper * noise * v.pulse);
        }
        float3 emission = sdfVolumeRamp(v, density) * (density * v.intensity);
        float extinction = density * v.extinction;
        if (v.kind == 1u) {
            float heightLight = lerp(0.52, 1.0, saturate(0.5 + 0.5 * p.y / v.halfExtent.y));
            emission *= v.extinction * heightLight;
        }
        float segment = exp(-extinction * stepLength);
        // The zero-absorption limit is stepLength, so pure emissive media remain visible.
        float integral = extinction > 1.0e-5 ? (1.0 - segment) / extinction : stepLength;
        radiance += transmission * emission * integral;
        transmission *= segment;
    }
    radianceOut = radiance;
    transmissionOut = transmission;
}

// Composites every bounded volume over a pixel's two shares, each clipped where its own rays end, so a volume never
// paints before the near plane or through solid geometry: the surface share, premultiplied by its coverage
// `surfaceCoverage`, between `nearDistance` (the ray distance where the ray crosses the camera's own near plane,
// worldNearDistance, zero for a camera whose image begins at its eye) and `surfaceDistance`, and the sky share beside it,
// premultiplied by the rest of the pixel, between `nearDistance` and `farDistance`. A volume composes over a share of
// weight w as w times its radiance plus its transmission times the share. Select the next farthest intersecting volume,
// integrate it, and composite immediately. This preserves the previous entry-distance ordering (including index ties)
// without per-pixel arrays or an unrolled copy of the integrator for every capacity slot. A volume that ends before the
// surface integrates once for both shares; one the surface clips integrates again over the surface share's span.
// Overlapping media still composite as whole volumes; this is not a combined-density integral through their overlap.
// `covered` is one where any volume composites over the ray's span. Nothing reads it: the composite alone integrates volumes,
// after the views pass, and a temporal view's reactivity is written by the views pass only, so a covered volume is not reactive.
float3 shadeVolumes(float3 surface, float surfaceCoverage, float surfaceDistance, float3 sky, float3 rayOrigin, float3 rayDirection, float nearDistance, float farDistance, uint2 pixel, out float covered) {
    covered = 0.0;
    bool surfaceShare = (surfaceCoverage > 0.0);
    bool skyShare = (surfaceCoverage < 1.0);
    float skyCoverage = (1.0 - surfaceCoverage);
    // The span a volume is selected over: the farther share's.
    float spanEnd = (skyShare ? farDistance : surfaceDistance);
    float dither = ((sdfR2Dither(pixel) * 2.0) - 1.0);
    float previousNear = 3.402823e+38;
    uint previousIndex = SdfVolumeCount;
    [loop] for (uint rank = 0u; rank < SdfVolumeCount; rank++) {
        uint selected = SdfVolumeCount;
        float selectedNear = -3.402823e+38;
        [loop] for (uint index = 0u; index < SdfVolumeCount; index++) {
            SdfVolumeData v = sdfLoadVolume(index);
            // PackVolumes writes a contiguous prefix and zeroes every trailing bound.
            if (all(v.halfExtent <= 0.0)) break;
            float3 localOrigin = sdfVolumeLocalPoint(v, rayOrigin);
            float3 localDirection = sdfVolumeLocalDirection(v, rayDirection);
            float2 interval = sdfVolumeSlabInterval(localOrigin, localDirection, v.halfExtent);
            if (min(interval.y, spanEnd) <= max(interval.x, nearDistance)) continue;
            bool beforeCursor = interval.x < previousNear || (interval.x == previousNear && index < previousIndex);
            bool nearerChoice = interval.x > selectedNear || (interval.x == selectedNear && index > selected);
            if (beforeCursor && (selected == SdfVolumeCount || nearerChoice)) {
                selected = index;
                selectedNear = interval.x;
            }
        }
        if (selected == SdfVolumeCount) break;
        SdfVolumeData v = sdfLoadVolume(selected);
        float3 localOrigin = sdfVolumeLocalPoint(v, rayOrigin);
        float3 localDirection = sdfVolumeLocalDirection(v, rayDirection);
        float2 interval = sdfVolumeSlabInterval(localOrigin, localDirection, v.halfExtent);
        float begin = max(interval.x, nearDistance);
        float skyEnd = min(interval.y, farDistance);
        float surfaceEnd = min(interval.y, surfaceDistance);
        v.intensity *= sdfVolumeIntensityScale(v);
        float3 radiance = float3(0.0, 0.0, 0.0);
        float transmission = 1.0;
        if (skyShare) {
            sdfIntegrateVolume(v, localOrigin, localDirection, begin, skyEnd, dither, radiance, transmission);
            sky = ((skyCoverage * radiance) + (transmission * sky));
        }
        if (surfaceShare && (surfaceEnd > begin)) {
            if (!skyShare || (surfaceEnd < skyEnd)) {
                sdfIntegrateVolume(v, localOrigin, localDirection, begin, surfaceEnd, dither, radiance, transmission);
            }
            surface = ((surfaceCoverage * radiance) + (transmission * surface));
        }
        covered = 1.0;
        previousNear = selectedNear;
        previousIndex = selected;
    }

    return (surface + sky);
}

#endif
