// Curvature shading and the world and object grid overlays.
#ifndef SHADE_SDF_SURFACE_SHADING_HLSLI
#define SHADE_SDF_SURFACE_SHADING_HLSLI
// Folds the stylized curvature terms into an already-lit surface color. Every term reads the curvature through the
// authored band: a ridge or cavity saturates at the band's low edge and the ink line spans the band, so a gain is a
// fraction in [0, 1] whatever the geometry's fillet radii (a 0.02-unit fillet has curvature 50; the raw value would
// blow every rounded edge to white). Cavity darkening scales the color down in concavities, the ridge light adds on
// convexities, and the ink outline lerps toward the ink color where the magnitude spikes.
float3 applyCurvatureShading(float3 shaded, float curvature) {
    float low = max(worldCurvatureInkLow(), 1.0e-3);
    float ridge = smoothstep(0.0, low, max(curvature, 0.0));
    float cavity = smoothstep(0.0, low, max(-curvature, 0.0));

    shaded *= (1.0 - (worldCurvatureCavity() * cavity));
    shaded += (worldCurvatureRim() * ridge);

    float ink = (worldCurvatureInk() * smoothstep(low, worldCurvatureInkHigh(), abs(curvature)));

    return lerp(shaded, worldCurvatureInkColor(), saturate(ink));
}
#ifdef SDF_SCREEN_SOURCES
// The world FLOOR grid (grid-locking §4b): two-scale frac bands on the floor's XZ, tinted (not replaced) toward a cool
// line color, with distance + grazing fades so the far field and skimming rays never moire. A line is drawn where
// EITHER axis sits near a cell boundary; the major band (4x pitch) reads heavier so distance counts at a glance.
// Guarded on SDF_SCREEN_SOURCES: it reads the grid rows from sdfScreenLights, bound only in that configuration.
float3 applyWorldFloorGrid(float3 color, float2 xz, float2 pitch, float3 rayDirection, float traveled) {
    if ((pitch.x <= 0.0) || (pitch.y <= 0.0)) {
        return color;
    }

    float2 minorEdge = min(frac(xz / pitch), (1.0 - frac(xz / pitch)));
    float2 majorEdge = min(frac(xz / (pitch * 4.0)), (1.0 - frac(xz / (pitch * 4.0))));
    float minorLine = (1.0 - smoothstep(0.0, 0.04, min(minorEdge.x, minorEdge.y)));
    float majorLine = (1.0 - smoothstep(0.0, 0.04, min(majorEdge.x, majorEdge.y)));
    float strength = max((minorLine * 0.45), (majorLine * 0.9));

    // Anti-moire (§4d): fade with distance (far pitch < 1px) and with grazing angle (floor normal = +Y).
    strength *= saturate(1.0 - (traveled / GridFadeDistance));
    strength *= saturate(abs(rayDirection.y) / GridGrazeCos);

    return lerp(color, GridWorldLineColor, (strength * 0.55));
}

// The OBJECT grid (grid-locking §4c): a FINITE lattice patch — the reference's OWN lattice, floor-projected around the
// guide within a bounded radius. The floor point is transformed into the reference's LOCAL frame and the frac bands
// are evaluated on its local XZ, so a rotated reference renders a correctly-rotated grid for free (the world->local
// transform bakes the rotation — no lines are rotated in screen space). Warm, so it reads distinct from the cool
// world floor grid it overlays; a radial fade keeps the patch finite and legible around the reference.
float3 applyObjectGrid(float3 color, float3 surfacePoint, float3 rayDirection, float floorY) {
    if (abs(surfacePoint.y - floorY) >= 0.02) {
        return color; // floor-projected: only paints the floor plane (it overlays the cool world grid)
    }

    float4 originRow = sdfScreenLights[SdfGridObjOrigin];
    float4 frame = sdfScreenLights[SdfGridObjFrame];
    float4 paramsRow = sdfScreenLights[SdfGridObjParams];
    float2 pitch = float2(originRow.w, paramsRow.x);
    float patchRadius = paramsRow.y;

    if ((pitch.x <= 0.0) || (pitch.y <= 0.0) || (patchRadius <= 0.0)) {
        return color;
    }

    float3 local = rotatePointByInverseQuaternion((surfacePoint - originRow.xyz), frame); // world -> reference-local
    float planar = length(local.xz);

    if (planar > patchRadius) {
        return color; // finite patch, not an infinite plane
    }

    float2 minorEdge = min(frac(local.xz / pitch), (1.0 - frac(local.xz / pitch)));
    float minorLine = (1.0 - smoothstep(0.0, 0.05, min(minorEdge.x, minorEdge.y)));
    float radialFade = saturate(1.0 - (planar / patchRadius));
    float graze = saturate(abs(rayDirection.y) / GridGrazeCos); // floor normal = +Y
    float strength = ((minorLine * radialFade) * graze);

    return lerp(color, GridObjectLineColor, (strength * 0.7));
}
#endif

#endif
