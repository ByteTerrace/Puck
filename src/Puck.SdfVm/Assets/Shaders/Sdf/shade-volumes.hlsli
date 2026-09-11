// Bounded flow/cloud volumes. Eleven float4 rows, paired with SdfWorldEngine.PackVolumes.
#ifndef SDF_SHADE_VOLUMES_HLSLI
#define SDF_SHADE_VOLUMES_HLSLI
struct SdfVolumeData {
    float3 position;
    int dynamicSlot;
    float4 rotation;
    float3 halfExtent;
    float axis;
    float width;
    float speed;
    uint seed;
    float steps;
    float intensity;
    float extinction;
    float pulseAmplitude;
    float pulseFrequency;
    int intensityLane;
    uint rampCount;
    uint kind;
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
    v.width = r3.x; v.speed = r3.y; v.seed = asuint(r3.z); v.steps = r3.w;
    float4 r4 = sdfVolumes[b + 4u];
    v.intensity = r4.x; v.extinction = r4.y; v.pulseAmplitude = r4.z; v.pulseFrequency = r4.w;
    float4 r5 = sdfVolumes[b + 5u];
    v.intensityLane = (int)r5.x; v.rampCount = (uint)r5.y; v.kind = (uint)r5.z;
    v.coverage = sdfVolumes[b + 10u].x; v.softness = sdfVolumes[b + 10u].y;
    [unroll] for (uint i = 0u; i < 4u; i++) v.ramp[i] = sdfVolumes[b + 6u + i];
    return v;
}
// Transforms a world point/direction into the volume's own frame: first undo the riding dynamic slot's rigid
// transform (when dynamicSlot >= 0), matching how a dynamic shape instance resolves its bound (sdf-vm.hlsli's
// rigidBasePosition), then undo the volume's own Position/Rotation offset within that frame. A direction only
// undoes the rotations (no translation).
float3 sdfVolumeLocalPoint(SdfVolumeData v, float3 worldPoint) {
    float3 p = worldPoint;

    if (v.dynamicSlot >= 0) {
        uint slot = (uint)v.dynamicSlot;
        float4 dynamicPosition = sdfDynamicTransforms[(3u * slot)];
        float4 dynamicOrientation = sdfDynamicTransforms[((3u * slot) + 1u)];

        p = rotatePointByInverseQuaternion((p - dynamicPosition.xyz), dynamicOrientation);
    }

    return rotatePointByInverseQuaternion((p - v.position), v.rotation);
}
float3 sdfVolumeLocalDirection(SdfVolumeData v, float3 worldDirection) {
    float3 d = worldDirection;

    if (v.dynamicSlot >= 0) {
        uint slot = (uint)v.dynamicSlot;
        float4 dynamicOrientation = sdfDynamicTransforms[((3u * slot) + 1u)];

        d = rotatePointByInverseQuaternion(d, dynamicOrientation);
    }

    return rotatePointByInverseQuaternion(d, v.rotation);
}
// An absent lane uses constant gain; a selected lane without a riding slot reads zero.
float sdfVolumeIntensityScale(SdfVolumeData v) {
    if (v.intensityLane < 0) return 1.0;
    if (v.dynamicSlot < 0) return 0.0;
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
// illumination; this does not trace cloud shadows or multiple scattering.
float sdfCloudDensity(SdfVolumeData v, float3 p, float clock) {
    float3 unit = p / v.halfExtent;
    float envelope = smoothstep(0.0, 0.3, 1.0 - dot(unit, unit));
    float3 q = (p + float3(clock * v.speed, 0.0, clock * v.speed * 0.21)) / v.width;
    float noise = 0.5 + 0.5 * (
        0.57 * sdfLatticeNoise3(q, v.seed) +
        0.28 * sdfLatticeNoise3(q * 2.03 + 7.1, v.seed + 19u) +
        0.15 * sdfLatticeNoise3(q * 4.07 - 3.7, v.seed + 53u));
    return envelope * smoothstep(1.0 - v.coverage - v.softness, 1.0 - v.coverage + v.softness, noise);
}
void sdfIntegrateVolume(SdfVolumeData v, float3 localOrigin, float3 localDirection, float tBegin, float tEnd,
    float clock, float dither, out float3 radianceOut, out float transmissionOut) {
    int steps = (int)v.steps;
    float stepLength = (tEnd - tBegin) / (float)steps;
    float3 radiance = 0.0;
    float transmission = 1.0;
    float pulse = 1.0 + v.pulseAmplitude * sin(2.0 * SDF_PI * v.pulseFrequency * clock);
    [loop] for (int i = 0; i < steps; i++) {
        float sampleT = tBegin + ((float)i + 0.5 + dither * 0.49) * stepLength;
        float3 p = localOrigin + localDirection * sampleT;
        float density;
        if (v.kind == 1u) {
            density = saturate(sdfCloudDensity(v, p, clock) * pulse);
        } else {
            float axial = (v.halfExtent.y - p.y) / v.axis;
            if (axial < 0.0 || axial >= 1.0) continue;
            float taper = 1.0 - axial;
            float radius = length(p.xz) / max(v.width * taper, 1.0e-6);
            float3 flow = float3(p.x, p.y + clock * v.speed, p.z) / v.width;
            float noise = 0.5 + 0.5 * sdfLatticeNoise3(flow, v.seed);
            density = saturate(exp(-radius * radius) * taper * noise * pulse);
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

// Composites every bounded volume whose slab intersects the ray — clipped to `surfaceDistance`
// so a volume never paints through solid geometry. Select the next farthest intersecting volume, integrate it,
// and composite immediately. This preserves the previous entry-distance ordering (including index ties) without
// per-pixel arrays or an unrolled copy of the integrator for every capacity slot. Overlapping media still composite
// as whole volumes; this is not a combined-density integral through their overlap.
float3 shadeVolumes(float3 color, float3 rayOrigin, float3 rayDirection, float surfaceDistance, uint2 pixel, float time) {
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
            if (min(interval.y, surfaceDistance) <= max(interval.x, 0.0)) continue;
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
        v.intensity *= sdfVolumeIntensityScale(v);
        float3 radiance; float transmission;
        sdfIntegrateVolume(v, localOrigin, localDirection, max(interval.x, 0.0), min(interval.y, surfaceDistance),
            time, dither, radiance, transmission);
        color = radiance + transmission * color;
        previousNear = selectedNear;
        previousIndex = selected;
    }

    return color;
}

#endif
