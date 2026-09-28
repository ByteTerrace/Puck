// The editor grid a view draws (GridOverlayState, carried in the pass block's grid values): the world lattice on the
// working plane or on every surface, and a captured reference's own lattice around it. Lines are measured in pixels at
// the surface, so they keep their width up close and far away, and a lattice whose lines would crowd closer than a few
// widths fades out rather than shimmering. The flags are GridOverlayFlags, generated as SDF_GRID_*.
#ifndef SHADE_SDF_GRID_HLSLI
#define SHADE_SDF_GRID_HLSLI

// A major line every this many minor lines, drawn heavier so distance counts at a glance.
static const float GridMajorEvery = 4.0;
static const float3 GridWorldLineColor = float3(0.34, 0.56, 0.95);
static const float3 GridObjectLineColor = float3(0.96, 0.66, 0.28);
// How far a line fully on its lattice covers the lit color: strong enough to read on any lit material.
static const float GridWorldLineOpacity = 0.85;
static const float GridObjectLineOpacity = 0.9;

// The coverage of one axis's lattice lines at `coordinate`: 1 on a line, 0 a line width away. `width` is the line width
// in world units at the surface. A pitch at or below zero draws nothing; a pitch under eight widths fades to nothing at
// four.
float sdfGridAxisCoverage(float coordinate, float pitch, float width) {
    if (pitch <= 0.0) {
        return 0.0;
    }

    float offset = abs(coordinate - (round(coordinate / pitch) * pitch));
    float onLine = (1.0 - smoothstep((0.5 * width), (1.5 * width), offset));
    float spacing = saturate((((pitch / max(width, 1.0e-6)) - 4.0) / 4.0));

    return (onLine * spacing);
}
// The minor and major lines across one axis.
float sdfGridLattice(float coordinate, float pitch, float width) {
    float minor = (0.5 * sdfGridAxisCoverage(coordinate, pitch, width));
    float major = sdfGridAxisCoverage(coordinate, (pitch * GridMajorEvery), (1.5 * width));

    return max(minor, major);
}
// The lattice in a plane with the two in-plane coordinates and their pitches.
float sdfGridPlane(float2 coordinates, float2 pitch, float width) {
    return max(sdfGridLattice(coordinates.x, pitch.x, width), sdfGridLattice(coordinates.y, pitch.y, width));
}
// The lattice projected along `normal`: the X and Z lines on a surface facing up or down, the Z and Y lines on one
// facing X, the X and Y lines on one facing Z, and a blend weighted by the normal between them, so a line on a ramp
// meets the same line on the floor at its foot.
float sdfGridProjected(float3 position, float3 normal, float3 pitch, float width) {
    float3 weights = pow(abs(normal), 4.0);

    weights /= max(((weights.x + weights.y) + weights.z), 1.0e-6);

    float floorLines = sdfGridPlane(position.xz, pitch.xz, width);
    float xWall = sdfGridPlane(position.zy, pitch.zy, width);
    float zWall = sdfGridPlane(position.xy, pitch.xy, width);

    return (((weights.y * floorLines) + (weights.x * xWall)) + (weights.z * zWall));
}

// The view's grids over the lit color at a surface point with its geometric normal. `footprint` is one pixel's world
// width at the hit (SdfPixel.pixelFootprint * t).
float3 sdfApplyGrid(float3 color, float3 surfacePoint, float3 normal, float3 rayDirection, float footprint) {
    uint flags = passGroup.gridFlags;

    if (flags == 0u) {
        return color;
    }

    float facing = abs(dot(normal, rayDirection));
    // A grazing surface stretches a pixel along the view, so the width widens to keep the drawn line one width on
    // screen, and the lines fade before they alias.
    float width = ((footprint * max(passGroup.gridLineWidth, 0.5)) / max(facing, 0.2));
    float graze = saturate((facing / 0.15));

    if ((flags & SDF_GRID_SURFACE) != 0u) {
        float coverage = sdfGridProjected(surfacePoint, normal, passGroup.gridWorldPitch, width);

        color = lerp(color, GridWorldLineColor, ((coverage * graze) * GridWorldLineOpacity));
    } else if ((flags & SDF_GRID_WORLD) != 0u) {
        // The working plane: a surface at the plane's height, within a band as wide as the drawn line and never under a
        // hundredth of a unit, facing up or down.
        float band = max((2.0 * width), 0.01);

        if ((abs(surfacePoint.y - passGroup.gridPlaneY) <= band) && (abs(normal.y) > 0.5)) {
            float coverage = sdfGridPlane(surfacePoint.xz, passGroup.gridWorldPitch.xz, width);

            color = lerp(color, GridWorldLineColor, ((coverage * graze) * GridWorldLineOpacity));
        }
    }

    if ((flags & SDF_GRID_OBJECT) != 0u) {
        // The reference's own lattice, on its faces and on everything around it inside the patch radius, fading to its
        // edge.
        float4 frame = passGroup.gridObjectFrame;
        float3 local = rotatePointByInverseQuaternion((surfacePoint - passGroup.gridObjectOrigin), frame);
        float radius = passGroup.gridObjectPatchRadius;
        float distanceFromOrigin = length(local);

        if ((radius > 0.0) && (distanceFromOrigin < radius)) {
            float3 localNormal = rotatePointByInverseQuaternion(normal, frame);
            float coverage = sdfGridProjected(local, localNormal, passGroup.gridObjectPitch, width);
            float fade = saturate((1.0 - (distanceFromOrigin / radius)));

            color = lerp(color, GridObjectLineColor, (((coverage * fade) * graze) * GridObjectLineOpacity));
        }
    }

    return color;
}

#endif
