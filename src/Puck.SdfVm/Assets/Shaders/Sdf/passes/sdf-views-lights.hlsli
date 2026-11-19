// The views pass's light tiles: each workgroup, one 8x8 tile of the render extent in every views kernel (the entry point's
// raster jobs and the binned jobs alike), narrows the view's light mask (the pass block's lightMask, SdfLightSelection)
// to the lights that can reach a surface point the tile shades, and its light walks visit only those
// (sdfLightWalkStart). The tile's hit points are bounded by two boxes, the hits nearer than the middle of the tile's
// depth range and the rest, so a tile across a silhouette bounds the object and what lies behind it apart rather than the
// whole span between them.
//
// The invariant: a light the tile leaves out adds nothing at any point either box holds. A point or spot light's falloff
// is exactly zero at and past its range (sdfPointReach), and its cone exactly zero outside the cone's angle; an occluder
// is taken as reaching SDF_LIGHT_OCCLUDER_REACH of its radii, the reach the view's selection already takes. Every other
// kind (a directional, a rim) reaches everything and is kept. The boxes hold every hit point of the tile exactly, the
// same point the light stage shades (the views stage passes the one it forms), so the only difference from walking the
// view's whole mask is the rounding of a light's response within SdfViewsLightReachMargin of where it falls to zero.
#ifndef PASSES_SDF_VIEWS_LIGHTS_HLSLI
#define PASSES_SDF_VIEWS_LIGHTS_HLSLI
#include "Shared/ordered-float.hlsli"
#include "../frame/sdf-lights.hlsli"

// The tile's nearest and farthest hit distance, each the bits of a non-negative float, whose unsigned order is its own.
groupshared uint sdfViewsLightDepths[2];
// The two boxes in ordered bits (puckOrderedFromFloat): the near hits' low corner and high corner, then the far hits'.
groupshared uint sdfViewsLightBoxes[12];
groupshared uint sdfViewsLightTile[4];

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

// The mask of the lights the tile's walks visit: the view's lights that can reach a hit point of the tile. `shades` says
// whether the view walks lights at all (its mode shades finally), uniform over the group; a view that does not keeps the
// view's mask unread. `lit` says whether this lane's pixel is a hit the light stage may shade, at `surfacePoint`, `t`
// along its ray. Every lane of the group calls it, a lane past the render extent included, since it holds the group's
// barriers.
uint4 sdfViewsTileLights(SdfPixel p, bool shades, bool lit, float3 surfacePoint, float t) {
    uint4 viewLights = passGroup.lightMask;

#if defined(SDF_VIEWS_OMIT_LIGHTS) || defined(SDF_VIEWS_OMIT_LIGHT_TILES)
    return viewLights;
#endif
    if (!shades) {
        return viewLights;
    }

    uint2 corner = (p.pixel % SDF_VISIBILITY_GROUP_EDGE);
    uint lane = ((corner.y * SDF_VISIBILITY_GROUP_EDGE) + corner.x);

    if (lane < 12u) {
        sdfViewsLightBoxes[lane] = (((lane % 6u) < 3u) ? PuckOrderedHighest : PuckOrderedLowest);
    }
    if (lane < 4u) {
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

            if ((sdfComponent(viewLights, word) & bit) == 0u) {
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
    }
    GroupMemoryBarrierWithGroupSync();

    return uint4(sdfViewsLightTile[0], sdfViewsLightTile[1], sdfViewsLightTile[2], sdfViewsLightTile[3]);
}

#endif
