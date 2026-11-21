// The views pass's light tiles: each workgroup, one 8x8 tile of the render extent in every views kernel (the entry point's
// raster jobs and the binned jobs alike), narrows the view's light masks (sdfViewLightMasks: the pass block's lightMask,
// SdfLightSelection, and every bound screen) to the lights and screens that can reach a surface point the tile shades, and
// its light walks visit only those (sdfLightWalkStart). The tile's hit points are bounded by two boxes, the hits nearer
// than the middle of the tile's depth range and the rest, so a tile across a silhouette bounds the object and what lies
// behind it apart rather than the whole span between them.
//
// The invariant: a light or screen the tile leaves out adds nothing at any point either box holds. A point or spot light's
// falloff is exactly zero at and past its range (sdfPointReach), and its cone exactly zero outside the cone's angle; an
// occluder is taken as reaching SDF_LIGHT_OCCLUDER_REACH of its radii, the reach the view's selection already takes; a
// screen lights only what lies in front of its face's plane, and nothing while its mean luminance is zero
// (sdfLightResponse). Every other kind (a directional, a rim) reaches everything and is kept. The boxes hold every hit
// point of the tile exactly, the same point the light stage shades (the views stage passes the one it forms), so the only
// difference from walking the view's whole masks is the rounding of a response within SdfViewsLightReachMargin of where
// it falls to zero.
#ifndef PASSES_SDF_VIEWS_LIGHTS_HLSLI
#define PASSES_SDF_VIEWS_LIGHTS_HLSLI
#include "Shared/ordered-float.hlsli"
#include "../frame/sdf-lights.hlsli"
#include "../shade/sdf-light.hlsli"

// The tile's nearest and farthest hit distance, each the bits of a non-negative float, whose unsigned order is its own.
groupshared uint sdfViewsLightDepths[2];
// The two boxes in ordered bits (puckOrderedFromFloat): the near hits' low corner and high corner, then the far hits'.
groupshared uint sdfViewsLightBoxes[12];
// The tile's light mask words, then its screen mask.
groupshared uint sdfViewsLightTile[5];

// The share a reach is widened by before a box is tested against it, past the rounding between this test and the
// response's own: a light is left out only where its response is zero by a margin.
static const float SdfViewsLightReachMargin = 1.0e-3;

// The squared distance from `center` to the box `low`..`high`; zero inside it.
float sdfViewsBoxDistanceSquared(float3 center, float3 low, float3 high) {
    float3 outside = max(max((low - center), (center - high)), 0.0);

    return dot(outside, outside);
}
// Whether a point, spot or occluder light at `position` can add anything at a point of the box `low`..`high`: whether
// the box meets the sphere of its reach and, for a spot, the sphere about the box meets its cone. The cone is where
// saturate(dot(-l, SpotAxis) + SpotOffset) is above zero (sdfPointReach): the directions from the light within the angle
// whose cosine is -SpotOffset / |SpotAxis| of the axis. A sphere lies wholly outside a cone of half-angle a when
// cos(a) * across - sin(a) * along exceeds its radius, `along` and `across` its centre's offset from the apex along and
// across the axis: that expression is never more than the centre's distance to the cone.
bool sdfViewsLightReachesBox(SdfLight light, float3 position, float3 low, float3 high) {
    float reach = (((light.Kind == SDF_LIGHT_POINT) ? light.Param : (SDF_LIGHT_OCCLUDER_REACH * light.Param)) * (1.0 + SdfViewsLightReachMargin));

    if (sdfViewsBoxDistanceSquared(position, low, high) > (reach * reach)) {
        return false;
    }

    float axisLength = length(light.SpotAxis);

    if ((light.Kind != SDF_LIGHT_POINT) || !(axisLength > 0.0) || (light.SpotOffset >= axisLength)) {
        return true;
    }

    float cosine = clamp((-light.SpotOffset / axisLength), -1.0, 1.0);
    float sine = sqrt(max((1.0 - (cosine * cosine)), 0.0));
    float3 offset = (((low + high) * 0.5) - position);
    float radius = (0.5 * length(high - low));
    float along = (dot(offset, light.SpotAxis) / axisLength);
    float across = sqrt(max((dot(offset, offset) - (along * along)), 0.0));

    return (((cosine * across) - (sine * along)) <= ((radius * (1.0 + SdfViewsLightReachMargin)) + (SdfViewsLightReachMargin * length(offset))));
}
// Whether a screen's face, at `origin` facing `facing`, has any point of the box `low`..`high` in front of its plane:
// the box's farthest reach along the facing, its centre's offset plus its half extent projected on the facing's
// magnitudes.
bool sdfViewsScreenFacesBox(float3 origin, float3 facing, float range, float3 low, float3 high) {
    float3 center = ((low + high) * 0.5);
    float3 extent = ((high - low) * 0.5);
    float3 offset = (center - origin);
    float reach = (range * (1.0 + SdfViewsLightReachMargin));

    return ((sdfViewsBoxDistanceSquared(origin, low, high) <= (reach * reach)) &&
        ((dot(facing, offset) + dot(abs(facing), extent)) > (-SdfViewsLightReachMargin * (length(offset) + length(extent)))));
}

// The masks of the lights and screens the tile's walks visit: the view's that can reach a hit point of the tile.
// `shades` says whether the view walks lights at all (its mode shades finally), uniform over the group; a view that does
// not keeps the view's masks unread. `lit` says whether this lane's pixel is a hit the light stage may shade, at
// `surfacePoint`, `t` along its ray. Every lane of the group calls it, a lane past the render extent included, since it
// holds the group's barriers.
SdfLightMasks sdfViewsTileLights(SdfPixel p, bool shades, bool lit, float3 surfacePoint, float t) {
    SdfLightMasks viewMasks = sdfViewLightMasks();

#if defined(SDF_VIEWS_OMIT_LIGHTS) || defined(SDF_VIEWS_OMIT_LIGHT_TILES)
    return viewMasks;
#endif
    if (!shades) {
        return viewMasks;
    }

    uint2 corner = (p.pixel % SDF_VISIBILITY_GROUP_EDGE);
    uint lane = ((corner.y * SDF_VISIBILITY_GROUP_EDGE) + corner.x);

    if (lane < 12u) {
        sdfViewsLightBoxes[lane] = (((lane % 6u) < 3u) ? PuckOrderedHighest : PuckOrderedLowest);
    }
    if (lane < 5u) {
        sdfViewsLightTile[lane] = 0u;
    }
    if (lane < 2u) {
        sdfViewsLightDepths[lane] = ((lane == 0u) ? 0xFFFFFFFFu : 0u);
    }
    GroupMemoryBarrierWithGroupSync();

    if (lit) {
        InterlockedMin(sdfViewsLightDepths[0], asuint(max(t, 0.0)));
        InterlockedMax(sdfViewsLightDepths[1], asuint(max(t, 0.0)));
    }
    GroupMemoryBarrierWithGroupSync();

    if (lit) {
        float split = (0.5 * (asfloat(sdfViewsLightDepths[0]) + asfloat(sdfViewsLightDepths[1])));
        uint box = ((t > split) ? 6u : 0u);

        [unroll]
        for (uint axis = 0u; (axis < 3u); axis++) {
            uint ordered = puckOrderedFromFloat(surfacePoint[axis]);

            InterlockedMin(sdfViewsLightBoxes[box + axis], ordered);
            InterlockedMax(sdfViewsLightBoxes[(box + 3u) + axis], ordered);
        }
    }
    GroupMemoryBarrierWithGroupSync();

    // A box no hit joined keeps its low corner above its high one.
    bool nearHeld = (sdfViewsLightBoxes[0] <= sdfViewsLightBoxes[3]);
    bool farHeld = (sdfViewsLightBoxes[6] <= sdfViewsLightBoxes[9]);

    if (nearHeld || farHeld) {
        float3 nearLow = float3(puckFloatFromOrdered(sdfViewsLightBoxes[0]), puckFloatFromOrdered(sdfViewsLightBoxes[1]), puckFloatFromOrdered(sdfViewsLightBoxes[2]));
        float3 nearHigh = float3(puckFloatFromOrdered(sdfViewsLightBoxes[3]), puckFloatFromOrdered(sdfViewsLightBoxes[4]), puckFloatFromOrdered(sdfViewsLightBoxes[5]));
        float3 farLow = float3(puckFloatFromOrdered(sdfViewsLightBoxes[6]), puckFloatFromOrdered(sdfViewsLightBoxes[7]), puckFloatFromOrdered(sdfViewsLightBoxes[8]));
        float3 farHigh = float3(puckFloatFromOrdered(sdfViewsLightBoxes[9]), puckFloatFromOrdered(sdfViewsLightBoxes[10]), puckFloatFromOrdered(sdfViewsLightBoxes[11]));
        uint count = worldLightCount();

        // A light a lane, striding by the group's lanes.
        [loop]
        for (uint index = lane; (index < count); index += (SDF_VISIBILITY_GROUP_EDGE * SDF_VISIBILITY_GROUP_EDGE)) {
            uint word = (index / 32u);
            uint bit = (1u << (index % 32u));

            if ((sdfComponent(viewMasks.lights, word) & bit) == 0u) {
                continue;
            }

            SdfLight light = sdfLights[index];
            bool keep = true;

            if ((light.Kind == SDF_LIGHT_POINT) || (light.Kind == SDF_LIGHT_OCCLUDER)) {
                float3 position = worldPointLightPosition(light);

                keep = ((nearHeld && sdfViewsLightReachesBox(light, position, nearLow, nearHigh))
                    || (farHeld && sdfViewsLightReachesBox(light, position, farLow, farHigh)));
            }
            if (keep) {
                InterlockedOr(sdfViewsLightTile[word], bit);
            }
        }

        // A screen a lane: the walk's own bound, its binding, its luminance and its face (sdfLightNext's screen half).
        if (lane < sdfScreenLightCount()) {
            if (screenSourceBound(lane) && any(sdfScreenLights[lane].rgb > 0.0)) {
                ScreenSurfaceData surface = worldScreenSurface(lane);
                float3 origin = surface.origin.xyz;
                float3 facing = normalize(cross(surface.right.xyz, surface.up.xyz));

                if ((nearHeld && sdfViewsScreenFacesBox(origin, facing, surface.origin.w, nearLow, nearHigh))
                    || (farHeld && sdfViewsScreenFacesBox(origin, facing, surface.origin.w, farLow, farHigh))) {
                    InterlockedOr(sdfViewsLightTile[4], (1u << lane));
                }
            }
        }
    }
    GroupMemoryBarrierWithGroupSync();

    SdfLightMasks masks;

    masks.lights = uint4(sdfViewsLightTile[0], sdfViewsLightTile[1], sdfViewsLightTile[2], sdfViewsLightTile[3]);
    masks.screens = sdfViewsLightTile[4];

    return masks;
}

#endif
