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
// The invariant: a light, screen or occluder the tile leaves out adds nothing at any point either box holds. A point or
// spot light's falloff is exactly zero at and past its range (sdfPointReach), and its cone exactly zero outside the
// cone's angle; a screen lights only what lies in front of its face's plane, and nothing while its mean luminance is zero
// (sdfLightResponse); and a point, spot or screen light fades to exactly zero where its light falls under the view's
// cutoff (sdfLightCutoffFade), so one whose bound over a box stays under it adds nothing there. Every other kind (a
// directional, a rim) reaches everything and is kept. An analytic occluder's ambient adds nothing at and past its Reach
// from its center, and its shadow nothing outside the hull of its grown bounding sphere and the light it falls from
// (frame/sdf-occluders.hlsli), so the tile keeps, once its lights and screens stand, those whose sphere of reach meets a
// box or whose shadow toward one of them can (sdfOccluderReachesWalk). The boxes hold every hit point of the tile exactly, the same point the light stage shades
// (the views stage passes the one it forms), so the only difference from walking the view's whole masks is the rounding
// of a response within SdfLightReachMargin of where it falls to zero (sdfLightReachesBox).
//
// A tile also leaves out a light the irradiance volume traced (sdfLightTraces) that the probes about none of its shaded
// pixels see: such a light's share of the probes is zero at every one of them (sdfViewsCellLightsSeen over the cells the
// tile's pixels blend and sdfViewsPixelLightsSeen hold every light whose share can be above zero at a pixel), and a
// traced point or spot light adds nothing where its share is zero
// (SdfLightInputs.none, which the light phase's door answers by before it runs a program). So in a room the walk visits
// the fixtures that light it, never those whose range crosses its walls and floors from the rooms beside it.
//
// A binned view's filter pass also resolves the tile's traced lights' shares once for the tile (sdfViewsStoreTileShares):
// each cell its pixels blend, its representative's share of each of the first SDF_SHADE_TILE_SHARED_LIGHTS traced lights
// the tile keeps, which a binned pixel's walk reads in place of its slots' words (sdfViewsLightVisibility). The shares are
// sdfViewsSlotShareOf's of the same slots the pixel blends, rounded to UNORM16, so the walk's answer is the slots' own to
// that rounding.
#ifndef PASSES_SDF_VIEWS_LIGHTS_HLSLI
#define PASSES_SDF_VIEWS_LIGHTS_HLSLI
#include "Shared/ordered-float.hlsli"
#include "../frame/sdf-lights.hlsli"
#include "../shade/sdf-light.hlsli"
#include "sdf-views-probes.hlsli"
#include "sdf-views-filter.hlsli"

#if SDF_SHADE_TILE_LIGHT_WORDS != 12
#error A tile's light masks are the four words of a light mask, the four of a screen mask and the four of an occluder mask.
#endif
#if (SDF_SHADE_TILE_SHARED_LIGHTS != 8) || ((SDF_VISIBILITY_GROUP_EDGE / SDF_VIEWS_PROBE_RATE) != 2) || (SDF_SHADE_TILE_SHARE_WORDS != (4 + (16 * (SDF_SHADE_TILE_SHARED_LIGHTS / 2))))
#error A tile's shares are a light mask and four words for each of the four by four cells its pixels blend, which its 64 lanes store a word each.
#endif

#if defined(SDF_VIEWS_BINNED)
// Points a binned views pixel's probes at its tile's traced-light shares (sdfViewsStoreTileShares), so its walk reads a
// resolved traced light's share of its slots from the tile's few words (sdfViewsLightVisibility); a pixel that reads an
// extra, or none, keeps its slots' own words.
void sdfViewsProbesTile(inout SdfMeshProbes probes, SdfPixel p) {
#if !defined(SDF_VIEWS_OMIT_LIGHTS) && !defined(SDF_VIEWS_OMIT_LIGHT_TILES)
    if (sdfMeshAnswers(probes) && (probes.slotCells.x != 0u)) {
        uint first = sdfShadeTileSharesWord(p.viewIndex, sdfShadeTileOf(p.pixel));

        probes.tileTraced = uint4(sdfShadeTiles[first], sdfShadeTiles[first + 1u], sdfShadeTiles[first + 2u], sdfShadeTiles[first + 3u]);
        probes.tileShares = (first + SDF_MESH_IRRADIANCE_LIGHT_WORDS);
    }
#endif
}
// A binned kernel's tile masks, as the filter pass decided them for the pixel's tile; the view's own in a mode that walks
// no light, which the filter pass wrote there too.
SdfLightMasks sdfViewsTileLights(SdfPixel p, bool shades, bool lit, float3 surfacePoint, float t) {
    SdfLightMasks masks = sdfViewLightMasks();

#if !defined(SDF_VIEWS_OMIT_LIGHTS) && !defined(SDF_VIEWS_OMIT_LIGHT_TILES)
    uint word = sdfShadeTileLightsWord(p.viewIndex, sdfShadeTileOf(p.pixel));

    masks.lights = uint4(sdfShadeTiles[word], sdfShadeTiles[word + 1u], sdfShadeTiles[word + 2u], sdfShadeTiles[word + 3u]);
    masks.screens = uint4(sdfShadeTiles[word + 4u], sdfShadeTiles[word + 5u], sdfShadeTiles[word + 6u], sdfShadeTiles[word + 7u]);
    masks.occluders = uint4(sdfShadeTiles[word + 8u], sdfShadeTiles[word + 9u], sdfShadeTiles[word + 10u], sdfShadeTiles[word + 11u]);
#endif
    return masks;
}
#else
// The tile's nearest and farthest hit distance, each the bits of a non-negative float, whose unsigned order is its own.
groupshared uint sdfViewsLightDepths[2];
// The two boxes in ordered bits (puckOrderedFromFloat): the near hits' low corner and high corner, then the far hits'.
groupshared uint sdfViewsLightBoxes[12];
// The tile's light mask words, then its screen mask words, then its occluder mask words.
groupshared uint sdfViewsLightTile[12];
// The lights of the tile's mask the volume traced, and the traced lights the probes about some shaded pixel of the tile
// see.
groupshared uint sdfViewsLightTraced[SDF_MESH_IRRADIANCE_LIGHT_WORDS];
groupshared uint sdfViewsLightSeen[SDF_MESH_IRRADIANCE_LIGHT_WORDS];

// Whether a screen's face, at `origin` facing `facing`, can light any point of the box `low`..`high`: whether the box
// has a point in front of its plane (the box's farthest reach along the facing, its centre's offset plus its half extent
// projected on the facing's magnitudes) within its range, where its light reaches the view's cutoff
// (sdfLightCutoffFade). A face of mean luminance `luminance` and weight `weight` (its area over pi) gives a point at most
// luminance * weight / (d^2 + weight) (sdfLightResponse), its facing and window at most one.
bool sdfViewsScreenFacesBox(float3 origin, float3 facing, float range, float luminance, float weight, float3 low, float3 high) {
    float3 center = ((low + high) * 0.5);
    float3 extent = ((high - low) * 0.5);
    float3 offset = (center - origin);
    float reach = (range * (1.0 + SdfLightReachMargin));
    float nearest = sdfBoxDistanceSquared(origin, low, high);

    return ((nearest <= (reach * reach))
        && (((luminance * weight * passGroup.exposure) * (1.0 + SdfLightReachMargin)) > (SDF_LIGHT_RANGE_THRESHOLD * (nearest + weight)))
        && ((dot(facing, offset) + dot(abs(facing), extent)) > (-SdfLightReachMargin * (length(offset) + length(extent)))));
}

#if defined(SDF_VIEWS_FILTER_PASS)
// Stores the tile's traced-light shares into the shade bins (SDF_SHADE_TILE_SHARE_WORDS words at sdfShadeTileSharesWord),
// which the binned views kernels read a traced light's share of a pixel's slots from (sdfViewsLightVisibility) in place
// of the slots' own agreement and list: the first SDF_SHADE_TILE_SHARED_LIGHTS of the traced lights the tile keeps
// (`kept`), as a light mask, then for each cell of the four by four the tile's pixels blend (from one cell before the
// tile's first to one past its last, row by row) that cell's representative's share of each of them in mask order
// (sdfViewsSlotShareOf), two UNORM16 a word; zero for a cell outside the view or whose representative no probe answers,
// which no pixel blends with a share above zero (sdfViewsProbesAt). A lane a cell and a pair of the lights, so the 64
// lanes take the sixteen cells' four pairs, and every share is one slot's words read once for the whole tile.
void sdfViewsStoreTileShares(SdfPixel p, uint lane, uint4 kept) {
    uint4 resolved = uint4(0u, 0u, 0u, 0u);
    uint2 order = uint2(0u, 0u);
    uint count = 0u;

    [unroll] for (uint word = 0u; (word < SDF_MESH_IRRADIANCE_LIGHT_WORDS); word++) {
        uint bits = kept[word];

        [loop] while ((bits != 0u) && (count < SDF_SHADE_TILE_SHARED_LIGHTS)) {
            uint index = ((word * 32u) + firstbitlow(bits));

            bits &= (bits - 1u);
            // The word is the unrolled loop's, a constant index.
            resolved[word] |= (1u << (index & 31u));
            order = ((count < 4u) ? uint2((order.x | (index << (8u * count))), order.y) : uint2(order.x, (order.y | (index << (8u * (count - 4u))))));
            count++;
        }
    }

    uint first = sdfShadeTileSharesWord(p.viewIndex, sdfShadeTileOf(p.pixel));

    if (lane < SDF_MESH_IRRADIANCE_LIGHT_WORDS) {
        sdfShadeTilesRW[first + lane] = sdfComponent(resolved, lane);
    }

    uint cellIndex = (lane / 4u);
    uint pair = (lane % 4u);
    int2 cell = (((int2(p.pixel / SDF_VISIBILITY_GROUP_EDGE) * int(SDF_VISIBILITY_GROUP_EDGE / SDF_VIEWS_PROBE_RATE)) - 1) + int2((cellIndex % 4u), (cellIndex / 4u)));
    float2 shares = float2(0.0, 0.0);

    if (((2u * pair) < count) && all(cell >= 0) && all(cell < int2(sdfViewsProbeCells()))) {
        uint record = sdfViewsProbeRecord(uint2(cell), p.viewIndex);
        uint stride = sdfViewsProbePlane();

        if ((sdfViewsProbeRecordBuffer[record + (SdfViewsProbeStateWord * stride)] & 0xFFFFu) != 0u) {
            uint4 partial;

            [unroll] for (uint entry = 0u; (entry < (SdfViewsProbePartialLights / 2u)); entry++) {
                partial[entry] = sdfViewsProbeRecordBuffer[record + ((SdfViewsProbePartialWord + entry) * stride)];
            }
            float allWeight = asfloat(sdfViewsProbeRecordBuffer[record + (SdfViewsProbeLightsWeightWord * stride)]);

            [unroll] for (uint side = 0u; (side < 2u); side++) {
                uint rank = ((2u * pair) + side);

                if (rank < count) {
                    uint index = ((((rank < 4u) ? order.x : order.y) >> (8u * (rank & 3u))) & 0xFFu);
                    uint lightWord = (index >> 5u);

                    shares[side] = sdfViewsSlotShareOf(sdfViewsProbeRecordBuffer[record + ((SdfViewsProbeLightsWord + lightWord) * stride)],
                        sdfViewsProbeRecordBuffer[record + ((SdfViewsProbeLightsAnyWord + lightWord) * stride)], allWeight, partial, index);
                }
            }
        }
    }

    uint2 bits = (uint2)round(saturate(shares) * 65535.0);

    sdfShadeTilesRW[(first + SDF_MESH_IRRADIANCE_LIGHT_WORDS) + ((cellIndex * (SDF_SHADE_TILE_SHARED_LIGHTS / 2u)) + pair)] = (bits.x | (bits.y << 16u));
}
#endif

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
    if (lane < 12u) {
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

            if (sdfLightPositional(light.Kind)) {
                float3 position = worldPointLightPosition(light);

                keep = ((nearHeld && sdfLightReachesBox(light, position, nearLow, nearHigh))
                    || (farHeld && sdfLightReachesBox(light, position, farLow, farHigh)));
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
                float luminance = dot(sdfScreenLights[screen].rgb, PuckRec709Luminance);
                // The face's area over pi, as the walk weighs it (sdfLightNext).
                float weight = (((4.0 * surface.right.w) * surface.up.w) / SDF_PI);

                if ((nearHeld && sdfViewsScreenFacesBox(origin, facing, surface.origin.w, luminance, weight, nearLow, nearHigh))
                    || (farHeld && sdfViewsScreenFacesBox(origin, facing, surface.origin.w, luminance, weight, farLow, farHigh))) {
                    InterlockedOr(sdfViewsLightTile[4u + (screen / 32u)], (1u << (screen % 32u)));
                }
            }
        }
    }
    GroupMemoryBarrierWithGroupSync();

    // An occluder a lane, striding by the group's lanes, once the tile's lights and screens stand: each of the view's whose
    // ambient or whose shadow toward one of them can reach a box (sdfOccluderReachesWalk), elsewhere adding nothing.
    if (nearHeld || farHeld) {
        float3 nearLow = float3(puckFloatFromOrdered(sdfViewsLightBoxes[0]), puckFloatFromOrdered(sdfViewsLightBoxes[1]), puckFloatFromOrdered(sdfViewsLightBoxes[2]));
        float3 nearHigh = float3(puckFloatFromOrdered(sdfViewsLightBoxes[3]), puckFloatFromOrdered(sdfViewsLightBoxes[4]), puckFloatFromOrdered(sdfViewsLightBoxes[5]));
        float3 farLow = float3(puckFloatFromOrdered(sdfViewsLightBoxes[6]), puckFloatFromOrdered(sdfViewsLightBoxes[7]), puckFloatFromOrdered(sdfViewsLightBoxes[8]));
        float3 farHigh = float3(puckFloatFromOrdered(sdfViewsLightBoxes[9]), puckFloatFromOrdered(sdfViewsLightBoxes[10]), puckFloatFromOrdered(sdfViewsLightBoxes[11]));
        uint4 tileLights = uint4(sdfViewsLightTile[0], sdfViewsLightTile[1], sdfViewsLightTile[2], sdfViewsLightTile[3]);
        uint4 tileScreens = uint4(sdfViewsLightTile[4], sdfViewsLightTile[5], sdfViewsLightTile[6], sdfViewsLightTile[7]);
        uint occluders = worldOccluderCount();

        [loop]
        for (uint occluder = lane; (occluder < occluders); occluder += (SDF_VISIBILITY_GROUP_EDGE * SDF_VISIBILITY_GROUP_EDGE)) {
            uint occluderWord = (occluder / 32u);
            uint occluderBit = (1u << (occluder % 32u));

            if ((sdfComponent(viewMasks.occluders, occluderWord) & occluderBit) == 0u) {
                continue;
            }

            SdfOccluder record = sdfOccluders[occluder];

            if ((nearHeld && sdfOccluderReachesWalk(record, tileLights, tileScreens, nearLow, nearHigh))
                || (farHeld && sdfOccluderReachesWalk(record, tileLights, tileScreens, farLow, farHigh))) {
                InterlockedOr(sdfViewsLightTile[8u + occluderWord], occluderBit);
            }
        }
    }

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
    uint4 seenTraced = (traced & uint4(sdfViewsLightSeen[0], sdfViewsLightSeen[1], sdfViewsLightSeen[2], sdfViewsLightSeen[3]));

    masks.lights = (uint4(sdfViewsLightTile[0], sdfViewsLightTile[1], sdfViewsLightTile[2], sdfViewsLightTile[3]) & ~(traced & ~seenTraced));
    masks.screens = uint4(sdfViewsLightTile[4], sdfViewsLightTile[5], sdfViewsLightTile[6], sdfViewsLightTile[7]);
    masks.occluders = uint4(sdfViewsLightTile[8], sdfViewsLightTile[9], sdfViewsLightTile[10], sdfViewsLightTile[11]);
#if defined(SDF_VIEWS_FILTER_PASS)
    sdfViewsStoreTileShares(p, lane, seenTraced);
#endif

    return masks;
}
#endif

#endif
