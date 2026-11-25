// The views pass's light tiles: each 8x8 tile of the render extent narrows the view's light masks (sdfViewLightMasks: the
// pass block's lightMask, SdfLightSelection, and every bound screen) to the lights and screens that can reach a surface
// point the tile shades, and its light walks visit only those (sdfLightWalkStart). The tile's hit points are bounded by
// two boxes, the hits nearer than the middle of the tile's depth range and the rest, so a tile across a silhouette bounds
// the object and what lies behind it apart rather than the whole span between them.
//
// One workgroup decides a tile (sdfViewsTileLights): the unsplit views kernels in their own group, and for a binned view
// the filter pass, which writes the masks into the tile's words of the shade bins (sdfShadeTileLightsWord); the binned
// views kernels read them there (SDF_VIEWS_BINNED), so they hold no barrier and none of the tile's state. The filter pass
// forms each pixel's surface point as the views stage does, from the same record.
//
// The invariant: a light or screen the tile leaves out adds nothing at any point either box holds. A point or spot light's
// falloff is exactly zero at and past its range (sdfPointReach), and its cone exactly zero outside the cone's angle; an
// occluder is taken as reaching SDF_LIGHT_OCCLUDER_REACH of its radii, the reach the view's selection already takes; a
// screen lights only what lies in front of its face's plane, and nothing while its mean luminance is zero
// (sdfLightResponse). Every other kind (a directional, a rim) reaches everything and is kept. The boxes hold every hit
// point of the tile exactly, the same point the light stage shades (the views stage passes the one it forms), so the only
// difference from walking the view's whole masks is the rounding of a response within SdfViewsLightReachMargin of where
// it falls to zero.
//
// A tile also leaves out a light the irradiance volume traced (sdfLightTraces) that the probes about none of its shaded
// pixels see: such a light's share of the probes is zero at every one of them (sdfViewsCellLightsSeen over the cells the
// tile's pixels blend and sdfViewsPixelLightsSeen hold every light whose share can be above zero at a pixel), and a
// traced point or spot light adds nothing where its share is zero
// (SdfLightInputs.unseen, which the light phase's door answers by before it runs a program). So in a room the walk visits
// the fixtures that light it, never those whose range crosses its walls and floors from the rooms beside it.
#ifndef PASSES_SDF_VIEWS_LIGHTS_HLSLI
#define PASSES_SDF_VIEWS_LIGHTS_HLSLI
#include "Shared/ordered-float.hlsli"
#include "../frame/sdf-lights.hlsli"
#include "../shade/sdf-light.hlsli"
#include "sdf-views-probes.hlsli"
#include "sdf-views-filter.hlsli"

#if SDF_SHADE_TILE_LIGHT_WORDS != 8
#error A tile's light masks are the four words of a light mask and the four of a screen mask.
#endif

#if defined(SDF_VIEWS_BINNED)
// A binned kernel's tile masks, as the filter pass decided them for the pixel's tile; the view's own in a mode that walks
// no light, which the filter pass wrote there too.
SdfLightMasks sdfViewsTileLights(SdfPixel p, bool shades, bool lit, float3 surfacePoint, float t) {
    SdfLightMasks masks = sdfViewLightMasks();

#if !defined(SDF_VIEWS_OMIT_LIGHTS) && !defined(SDF_VIEWS_OMIT_LIGHT_TILES)
    uint word = sdfShadeTileLightsWord(p.viewIndex, sdfShadeTileOf(p.pixel));

    masks.lights = uint4(sdfShadeTiles[word], sdfShadeTiles[word + 1u], sdfShadeTiles[word + 2u], sdfShadeTiles[word + 3u]);
    masks.screens = uint4(sdfShadeTiles[word + 4u], sdfShadeTiles[word + 5u], sdfShadeTiles[word + 6u], sdfShadeTiles[word + 7u]);
#endif
    return masks;
}
#else
// The tile's nearest and farthest hit distance, each the bits of a non-negative float, whose unsigned order is its own.
groupshared uint sdfViewsLightDepths[2];
// The two boxes in ordered bits (puckOrderedFromFloat): the near hits' low corner and high corner, then the far hits'.
groupshared uint sdfViewsLightBoxes[12];
// The tile's light mask words, then its screen mask words.
groupshared uint sdfViewsLightTile[8];
// The lights of the tile's mask the volume traced, and the traced lights the probes about some shaded pixel of the tile
// see.
groupshared uint sdfViewsLightTraced[SDF_MESH_IRRADIANCE_LIGHT_WORDS];
groupshared uint sdfViewsLightSeen[SDF_MESH_IRRADIANCE_LIGHT_WORDS];

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
// not keeps the view's masks unread. `lit` says whether this lane's pixel is a hit whose shading walks lights, at
// `surfacePoint`, `t` along its ray. Every lane of the group calls it, a lane past the render extent included, since it
// holds the group's barriers. Each lane joins the group's words with atomics of its own: reducing across the wave first
// costs the lanes as much, and holds enough more live to cost the unsplit kernels an occupancy step.
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
    if (lane < 8u) {
        sdfViewsLightTile[lane] = 0u;
    }
    if (lane < SDF_MESH_IRRADIANCE_LIGHT_WORDS) {
        sdfViewsLightTraced[lane] = 0u;
        sdfViewsLightSeen[lane] = 0u;
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
                if (sdfLightTraces(light, index)) {
                    InterlockedOr(sdfViewsLightTraced[word], bit);
                }
            }
        }

        // A screen a lane, striding by the group's lanes: the walk's own bound, its luminance and its face (sdfLightNext's
        // screen half). Whether its source is bound is the walk's to ask: a source binds without moving the lighting the
        // filter pass stands on.
        uint screens = sdfScreenLightCount();

        [loop]
        for (uint screen = lane; (screen < screens); screen += (SDF_VISIBILITY_GROUP_EDGE * SDF_VISIBILITY_GROUP_EDGE)) {
            if (any(sdfScreenLights[screen].rgb > 0.0)) {
                ScreenSurfaceData surface = worldScreenSurface(screen);
                float3 origin = surface.origin.xyz;
                float3 facing = normalize(cross(surface.right.xyz, surface.up.xyz));

                if ((nearHeld && sdfViewsScreenFacesBox(origin, facing, surface.origin.w, nearLow, nearHigh))
                    || (farHeld && sdfViewsScreenFacesBox(origin, facing, surface.origin.w, farLow, farHigh))) {
                    InterlockedOr(sdfViewsLightTile[4u + (screen / 32u)], (1u << (screen % 32u)));
                }
            }
        }
    }
    GroupMemoryBarrierWithGroupSync();

    // The traced lights the tile kept, the same on every lane, so a tile that kept none (the open valley) reads no probe
    // record here. The tile's pixels blend the tile's own cells and the ring of cells about them, a cell a lane; a pixel
    // that reads an extra or no slot adds its own.
    uint4 traced = uint4(sdfViewsLightTraced[0], sdfViewsLightTraced[1], sdfViewsLightTraced[2], sdfViewsLightTraced[3]);

    if (any(traced != 0u)) {
        uint ringEdge = ((SDF_VISIBILITY_GROUP_EDGE / SDF_VIEWS_PROBE_RATE) + 2u);
        uint4 seen = uint4(0u, 0u, 0u, 0u);

        if (lane < (ringEdge * ringEdge)) {
            seen = sdfViewsCellLightsSeen(((int2(p.pixel / SDF_VISIBILITY_GROUP_EDGE) * int(SDF_VISIBILITY_GROUP_EDGE / SDF_VIEWS_PROBE_RATE)) + (int2((lane % ringEdge), (lane / ringEdge)) - 1)), p.viewIndex);
        }
        if (lit) {
            seen |= sdfViewsPixelLightsSeen(p);
        }
        seen &= traced;

        [unroll]
        for (uint word = 0u; (word < SDF_MESH_IRRADIANCE_LIGHT_WORDS); word++) {
            if (seen[word] != 0u) {
                InterlockedOr(sdfViewsLightSeen[word], seen[word]);
            }
        }
    }
    GroupMemoryBarrierWithGroupSync();

    SdfLightMasks masks;

    masks.lights = (uint4(sdfViewsLightTile[0], sdfViewsLightTile[1], sdfViewsLightTile[2], sdfViewsLightTile[3])
        & ~(traced & ~uint4(sdfViewsLightSeen[0], sdfViewsLightSeen[1], sdfViewsLightSeen[2], sdfViewsLightSeen[3])));
    masks.screens = uint4(sdfViewsLightTile[4], sdfViewsLightTile[5], sdfViewsLightTile[6], sdfViewsLightTile[7]);

    return masks;
}
#endif

#endif
