// The field pass (SdfWorldPackage.WithField): the residency's field evaluated at every render pixel, through the field
// interpreter the mesh bake samples (mapCore, its one call site in this kernel), for the two field views the composite
// lays over the view (passes/sdf-composite.comp.hlsl):
// - the slicing plane (fieldStyle.z, Puck.SdfVm.SdfFieldPlane): where the pixel's ray meets the plane, the signed distance
//   in warm bands outside and cool bands inside, darkening toward the surface, with a white surface line; or |grad d|
//   from four tetrahedral taps, white where it is one, blue where the field is a bound, red where it overestimates, with a
//   black surface line. Behind the pixel's surface the plane keeps SDF_FIELD_HIDDEN of its opacity.
// - the proximity view (SDF_DEBUG_VIEW_PROXIMITY): at the pixel's visible surface, the distance to the nearest other
//   object's field (an object is SdfMeshDraw.ObjectKey, which each field entry carries), in rings that fade out past
//   SDF_FIELD_PROXIMITY_BANDS bands, a white line where they touch; and hot where another object's solid lies inside the
//   surface's own: the ray is marched on into the surface's object while the intersection field max(own, other) stays
//   above the view's tolerance, and the deepest it reads below it sets the glow.
// The field is the union of the field entries (Puck.SdfVm.SdfFieldEntries): each a run of program instances, walked in its
// own frame (a placed copy's template in the copy's frame and scale), bounded by a sphere. Each group of
// SDF_FIELD_TILE_EDGE squared pixels first keeps the entries whose spheres may answer one of its points (the plane's
// points, the visible surfaces and their march segments), then each query walks only those, skipping an entry whose
// sphere cannot beat the query's best; a tile that keeps more than SDF_FIELD_TILE_ENTRIES walks every entry.
// Each pixel writes one word of the field colours: red, green and blue display-encoded in the low bytes, the share of the
// pixel they cover in the high byte.
#define SDF_DYNAMIC_TRANSFORMS
#define SDF_INSTANCE_RUNS
#define SDF_GLYPH_ATLAS
#include "sdf-world.hlsli"
#include "../frame/sdf-visibility.hlsli"
#include "Shared/transfer.hlsli"

// KEEP IN SYNC with SdfFieldEntries' flags.
static const uint SdfFieldWorldSegmentsFlag = 1u;
static const uint SdfFieldBodyFlag = 2u;
static const uint SdfFieldSlotShift = 4u;
// KEEP IN SYNC with SdfFieldPlaneKind and SdfFieldPanel.
static const uint SdfFieldPlaneAxis = 1u;
static const uint SdfFieldPlaneCamera = 2u;
static const uint SdfFieldPlaneEye = 3u;
static const uint SdfFieldPlanePointer = 4u;
static const uint SdfFieldPanelGradient = 1u;
static const uint SdfFieldPanelBoth = 2u;
// The share of the plane's opacity it keeps behind a surface: enough to place it by, not enough to hide the surface.
static const float SDF_FIELD_HIDDEN = 0.2;
// The bands the proximity rings reach before they fade out, and the share of the view they cover.
static const float SDF_FIELD_PROXIMITY_BANDS = 8.0;
static const float SDF_FIELD_PROXIMITY_OPACITY = 0.85;
// The overlap depth at which the proximity view's glow turns fully yellow.
static const float SDF_FIELD_HOT_DEPTH = 0.05;
// The most queries one pixel runs: the visible surface, its march and the plane's four taps.
static const uint SDF_FIELD_MARCH_STEPS = 10u;
static const uint SDF_FIELD_MAX_QUERIES = (1u + SDF_FIELD_MARCH_STEPS + 4u);
// A key no entry carries, for a query that leaves no object out.
static const uint SdfFieldNoObject = 0xFFFFFFFFu;
static const uint SdfFieldTilePixels = (SDF_FIELD_TILE_EDGE * SDF_FIELD_TILE_EDGE);

struct SdfFieldEntry {
    float4 sphere;
    float4 frame;
    float4 orientation;
    uint4 meta;
};

groupshared uint sdfFieldTileEntries[SDF_FIELD_TILE_ENTRIES];
groupshared uint sdfFieldTileCount;
// The tile's box of query points as order-preserving words (sdfFieldOrdered): low xyz, then high xyz.
groupshared uint sdfFieldTileBox[6];
// The tile's upper bound on the plane's field, ordered.
groupshared uint sdfFieldTileUpper;
// Each pixel's plane and proximity values, which its neighbours read for their lines' width; NaN where none.
groupshared float sdfFieldTilePlane[SdfFieldTilePixels];
groupshared float sdfFieldTileProximity[SdfFieldTilePixels];

uint sdfFieldEntryCount() {
    uint count;
    uint stride;

    sdfFieldEntries.GetDimensions(count, stride);
    return min((uint)passGroup.fieldScope.x, (count / SDF_FIELD_ENTRY_ROWS));
}
SdfFieldEntry sdfFieldEntryAt(uint index) {
    uint row = (index * SDF_FIELD_ENTRY_ROWS);
    SdfFieldEntry entry;

    entry.sphere = sdfFieldEntries[row];
    entry.frame = sdfFieldEntries[row + 1u];
    entry.orientation = sdfFieldEntries[row + 2u];
    entry.meta = asuint(sdfFieldEntries[row + 3u]);
    return entry;
}
// Whether an entry is a body the proximity view leaves out without its bodies option: a moving copy, or the seat's own
// character (the slots its fade names, passGroup.stampFade).
bool sdfFieldEntryIsBody(uint4 meta) {
    int slot = (((int)(meta.w >> SdfFieldSlotShift)) - 1);
    float3 fade = passGroup.stampFade;

    return (((meta.w & SdfFieldBodyFlag) != 0u) || ((fade.y > 0.0) && (slot >= int(fade.x)) && (slot < int(fade.x + fade.y))));
}
// A float as a word that orders as the float does, for the tile's atomic bounds.
uint sdfFieldOrdered(float value) {
    uint bits = asuint(value);

    return (((bits & 0x80000000u) != 0u) ? ~bits : (bits | 0x80000000u));
}
float sdfFieldUnordered(uint key) {
    return asfloat(((key & 0x80000000u) != 0u) ? (key & 0x7FFFFFFFu) : ~key);
}
void sdfFieldGrowBox(float3 low, float3 high) {
    uint previous;

    InterlockedMin(sdfFieldTileBox[0], sdfFieldOrdered(low.x), previous);
    InterlockedMin(sdfFieldTileBox[1], sdfFieldOrdered(low.y), previous);
    InterlockedMin(sdfFieldTileBox[2], sdfFieldOrdered(low.z), previous);
    InterlockedMax(sdfFieldTileBox[3], sdfFieldOrdered(high.x), previous);
    InterlockedMax(sdfFieldTileBox[4], sdfFieldOrdered(high.y), previous);
    InterlockedMax(sdfFieldTileBox[5], sdfFieldOrdered(high.z), previous);
}

// The field of every entry at p, split in two: the least of the entries of object `own`, and the least of the rest. Each
// entry is walked as its run of instances in its own frame; one whose sphere cannot beat the least it would join is
// skipped, and with skipBodies so is a body (sdfFieldEntryIsBody). Distances at or past SDF_FAR_DISTANCE answer nothing.
void sdfFieldQuery(float3 p, uint own, bool skipBodies, bool overflow, out float ownDistance, out float otherDistance) {
    ownDistance = SDF_FAR_DISTANCE;
    otherDistance = SDF_FAR_DISTANCE;

    uint count = (overflow ? sdfFieldEntryCount() : sdfFieldTileCount);

    [loop]
    for (uint candidate = 0u; (candidate < count); candidate++) {
        SdfFieldEntry entry = sdfFieldEntryAt(overflow ? candidate : sdfFieldTileEntries[candidate]);
        bool mine = (entry.meta.z == own);
        float best = (mine ? ownDistance : otherDistance);

        if (skipBodies && !mine && sdfFieldEntryIsBody(entry.meta)) {
            continue;
        }
        if ((entry.sphere.w >= 0.0) && ((length(p - entry.sphere.xyz) - entry.sphere.w) >= best)) {
            continue;
        }

        float scale = ((abs(entry.frame.w) > 1.0e-12) ? entry.frame.w : 1.0);
        float3 local = (rotatePointByInverseQuaternion((p - entry.frame.xyz), entry.orientation) / scale);

        sdfInstanceRunFirst = entry.meta.x;
        sdfInstanceRunEnd = entry.meta.y;
        sdfMapOmitWorldSegments = ((entry.meta.w & SdfFieldWorldSegmentsFlag) == 0u);

        SdfHit hit = mapCore(local, SDF_INSTANCE_MASK_ALL, false);
        float distance = ((hit.distance < SDF_FAR_DISTANCE) ? (hit.distance * abs(scale)) : SDF_FAR_DISTANCE);

        sdfWorkSteps++;
        if (mine) {
            ownDistance = min(ownDistance, distance);
        } else {
            otherDistance = min(otherDistance, distance);
        }
    }
}

// IQ's distance palette (iquilezles.org, "interior distance"): warm outside, cool inside, darkening toward the surface over
// four bands, a band every `band` metres, and a white surface line `width` metres of field wide.
float3 sdfFieldDistanceColour(float d, float band, float width) {
    float3 colour = ((d > 0.0) ? float3(0.9, 0.6, 0.3) : float3(0.65, 0.85, 1.0));

    colour *= (1.0 - exp(-abs(d) / (4.0 * band)));
    colour *= (0.8 + (0.2 * cos((6.28318531 * d) / band)));
    return lerp(colour, float3(1.0, 1.0, 1.0), (1.0 - smoothstep(0.5 * width, 1.5 * width, abs(d))));
}
// |grad d|: white within two percent of one, blue toward a half and below (a bound), red toward one and a half and above
// (an overestimate), darker inside, with a black surface line. SdfFieldSlice.GradientTolerance is the PNG's twin.
float3 sdfFieldGradientColour(float d, float magnitude, float width) {
    float off = (magnitude - 1.0);
    float3 colour = ((abs(off) <= 0.02)
        ? float3(1.0, 1.0, 1.0)
        : ((off < 0.0)
            ? lerp(float3(1.0, 1.0, 1.0), float3(0.10, 0.30, 1.0), saturate(-off / 0.5))
            : lerp(float3(1.0, 1.0, 1.0), float3(1.0, 0.15, 0.10), saturate(off / 0.5))));

    colour *= ((d < 0.0) ? 0.8 : 1.0);
    return lerp(colour, float3(0.0, 0.0, 0.0), (1.0 - smoothstep(0.5 * width, 1.5 * width, abs(d))));
}
// The neighbours a pixel's line width reads in the tile's values: the one beside it and the one below it, or above and to
// the left at the tile's last column and row.
uint sdfFieldAcross(uint2 local) {
    uint index = ((local.y * SDF_FIELD_TILE_EDGE) + local.x);

    return (((local.x + 1u) < SDF_FIELD_TILE_EDGE) ? (index + 1u) : (index - 1u));
}
uint sdfFieldDown(uint2 local) {
    uint index = ((local.y * SDF_FIELD_TILE_EDGE) + local.x);

    return (((local.y + 1u) < SDF_FIELD_TILE_EDGE) ? (index + SDF_FIELD_TILE_EDGE) : (index - SDF_FIELD_TILE_EDGE));
}
// The field width of one pixel: the larger change to its two neighbours, held to four pixels' footprint, or `fallback`
// (one pixel's footprint) where a neighbour holds none.
float sdfFieldPixelWidth(float here, float across, float down, float fallback) {
    float width = max(abs(across - here), abs(down - here));

    return ((isfinite(width) && (width > 0.0)) ? min(width, (4.0 * fallback)) : fallback);
}
// Lays `colour` at `alpha` over a layer premultiplied by its coverage.
float4 sdfFieldOver(float4 under, float3 colour, float alpha) {
    return float4(((colour * alpha) + (under.rgb * (1.0 - alpha))), (alpha + (under.a * (1.0 - alpha))));
}

[numthreads(SDF_FIELD_TILE_EDGE, SDF_FIELD_TILE_EDGE, 1)]
void CSMain(uint3 id : SV_DispatchThreadID, uint3 groupLocal : SV_GroupThreadID, uint lane : SV_GroupIndex) {
    uint2 extent = passGroup.imageExtent;
    uint2 pixel = id.xy;
    bool inside = all(pixel < extent);
    uint panelKind = (uint)passGroup.fieldStyle.w;
    uint planeKind = (uint)passGroup.fieldStyle.z;
    bool proximity = (passGroup.debugMode == SDF_DEBUG_VIEW_PROXIMITY);
    float band = max(passGroup.fieldStyle.x, 1.0e-4);
    float proximityBand = max(passGroup.debugOptions.z, 1.0e-4);
    float proximityReach = (SDF_FIELD_PROXIMITY_BANDS * proximityBand);
    float tolerance = max(passGroup.debugOptions.x, 0.0);

    sdfProgramLayout = sdfLoadProgramLayout();
    if (lane == 0u) {
        sdfFieldTileCount = 0u;
        sdfFieldTileUpper = sdfFieldOrdered(SDF_FAR_DISTANCE);
        sdfFieldTileBox[0] = 0xFFFFFFFFu;
        sdfFieldTileBox[1] = 0xFFFFFFFFu;
        sdfFieldTileBox[2] = 0xFFFFFFFFu;
        sdfFieldTileBox[3] = 0u;
        sdfFieldTileBox[4] = 0u;
        sdfFieldTileBox[5] = 0u;
    }
    GroupMemoryBarrierWithGroupSync();

    // The pixel's ray without the sample's jitter, and its visible surface.
    ViewportData view = worldView();

    view.lens.yz = passGroup.frustumOffset;

    float3 origin = view.position.xyz;
    float3 direction = cameraRayDirection(view, ((float2(pixel) + 0.5) / float2(max(extent, uint2(1u, 1u)))));
    float footprint = ((2.0 * view.right.w) / max((float)extent.y, 1.0));
    float surface = 0.0;
    uint own = SdfFieldNoObject;
    bool surfaceShown = false;

    if (inside) {
        uint record = sdfVisibilityRecord(pixel, 0u, extent);
        SdfVisibility visibility = sdfLoadVisibility(record);
        SdfMeshHit hit;

        if (sdfVisibilityHit(visibility)) {
            surface = visibility.t;
        }
        // The visible draw names a record only through the hit door; a body takes part only with the bodies option.
        if (proximity && (sdfVisibilityKind(visibility.identity) == SDF_VISIBILITY_KIND_MESH) &&
            sdfMeshResolveHit(sdfVisibilitySource(visibility.identity), sdfVisibilityRecordBuffer[record + SdfVisibilityTriangleWord], hit)) {
            uint selection = sdfMeshRecordWord(hit.record + SDF_MESH_SELECTION_WORD + 3u);
            int slot = asint(sdfMeshRecordWord(hit.record + SDF_MESH_FRAME_SLOT_WORD));
            float3 fade = passGroup.stampFade;
            bool seated = ((fade.y > 0.0) && (slot >= int(fade.x)) && (slot < int(fade.x + fade.y)));

            own = (sdfMeshRecordWord(hit.record + SDF_MESH_FLAGS_WORD) >> SDF_MESH_OBJECT_KEY_SHIFT);

            bool copy = (((selection & SDF_MESH_DYNAMIC_FLAG) != 0u) && (own != 0u) && ((own & SDF_MESH_GROUP_OBJECT_KEY_BIT) == 0u));

            surfaceShown = (!(seated || copy) || (passGroup.debugOptions.y > 0.0));
        }
    }

    // Where the ray meets the plane, by the plane's kind.
    bool planeShown = false;
    float planeT = 0.0;
    float3 planeNormal = float3(0.0, 1.0, 0.0);
    float planeOffset = 0.0;

    if (inside && (planeKind != 0u)) {
        bool placed = true;

        if (planeKind == SdfFieldPlaneAxis) {
            planeNormal = passGroup.fieldPlane.xyz;
            planeOffset = passGroup.fieldPlane.w;
        } else if (planeKind == SdfFieldPlaneCamera) {
            planeNormal = view.forward.xyz;
            planeOffset = dot((origin + (view.forward.xyz * passGroup.fieldPlane.w)), planeNormal);
        } else if (planeKind == SdfFieldPlaneEye) {
            planeOffset = (origin.y + passGroup.fieldPlane.w);
        } else if ((planeKind == SdfFieldPlanePointer) && all(passGroup.fieldScope.yz >= 0.0)) {
            // Through the surface under the pointer, or two metres along its ray where it meets none.
            uint2 under = min((uint2)(passGroup.fieldScope.yz * float2(extent)), (extent - 1u));
            SdfVisibility pointed = sdfLoadVisibility(sdfVisibilityRecord(under, 0u, extent));
            float3 pointerRay = cameraRayDirection(view, ((float2(under) + 0.5) / float2(extent)));
            float3 pointerPoint = (origin + (pointerRay * (sdfVisibilityHit(pointed) ? pointed.t : 2.0)));

            planeNormal = ((dot(passGroup.fieldPlane.xyz, passGroup.fieldPlane.xyz) > 0.0) ? passGroup.fieldPlane.xyz : view.forward.xyz);
            planeOffset = dot(pointerPoint, planeNormal);
        } else {
            placed = false;
        }

        float facing = dot(direction, planeNormal);

        if (placed && (abs(facing) > 1.0e-6)) {
            planeT = ((planeOffset - dot(origin, planeNormal)) / facing);
            planeShown = (planeT > max(worldNearDistance(view), 1.0e-4));
        }
    }

    bool gradient = (planeShown && ((panelKind == SdfFieldPanelGradient) || ((panelKind == SdfFieldPanelBoth) && ((pixel.x * 2u) >= extent.x))));
    float3 planePoint = (origin + (direction * planeT));
    float tap = clamp((0.25 * footprint * planeT), 5.0e-4, 0.05);
    float3 surfacePoint = (origin + (direction * surface));

    // The tile's box of every point a query of it may read, and the plane's upper bound over the box.
    if (planeShown) {
        sdfFieldGrowBox(planePoint - tap, planePoint + tap);
    }
    if (surfaceShown) {
        sdfFieldGrowBox(min(surfacePoint, surfacePoint + (direction * (2.0 * proximityReach))), max(surfacePoint, surfacePoint + (direction * (2.0 * proximityReach))));
    }
    GroupMemoryBarrierWithGroupSync();

    float3 boxLow = float3(sdfFieldUnordered(sdfFieldTileBox[0]), sdfFieldUnordered(sdfFieldTileBox[1]), sdfFieldUnordered(sdfFieldTileBox[2]));
    float3 boxHigh = float3(sdfFieldUnordered(sdfFieldTileBox[3]), sdfFieldUnordered(sdfFieldTileBox[4]), sdfFieldUnordered(sdfFieldTileBox[5]));
    bool tileShows = (sdfFieldTileBox[0] != 0xFFFFFFFFu);
    uint entries = sdfFieldEntryCount();

    if (tileShows && (planeKind != 0u)) {
        // Every bounded entry's farthest reach over the box bounds the plane's field there.
        [loop]
        for (uint index = lane; (index < entries); index += SdfFieldTilePixels) {
            float4 sphere = sdfFieldEntries[index * SDF_FIELD_ENTRY_ROWS];

            if (sphere.w >= 0.0) {
                float3 far = max(abs(sphere.xyz - boxLow), abs(boxHigh - sphere.xyz));
                uint previous;

                InterlockedMin(sdfFieldTileUpper, sdfFieldOrdered(length(far) + sphere.w), previous);
            }
        }
    }
    GroupMemoryBarrierWithGroupSync();

    // The entries whose spheres reach within the threshold of the box: the plane's upper bound, or the farthest the
    // proximity rings and march read.
    float threshold = max(((planeKind != 0u) ? sdfFieldUnordered(sdfFieldTileUpper) : 0.0), (proximity ? (2.0 * proximityReach) : 0.0));

    if (tileShows) {
        [loop]
        for (uint index = lane; (index < entries); index += SdfFieldTilePixels) {
            float4 sphere = sdfFieldEntries[index * SDF_FIELD_ENTRY_ROWS];
            float3 near = max(max((boxLow - sphere.xyz), (sphere.xyz - boxHigh)), 0.0);

            if ((sphere.w < 0.0) || ((length(near) - sphere.w) <= threshold)) {
                uint slot;

                InterlockedAdd(sdfFieldTileCount, 1u, slot);
                if (slot < SDF_FIELD_TILE_ENTRIES) {
                    sdfFieldTileEntries[slot] = index;
                }
            }
        }
    }
    GroupMemoryBarrierWithGroupSync();

    bool overflow = (sdfFieldTileCount > SDF_FIELD_TILE_ENTRIES);

    // The pixel's queries, one walk a step: the visible surface and its march into its own object while the proximity
    // view shows, then the plane's point, or its four taps for the gradient.
    float surfaceOther = SDF_FAR_DISTANCE;
    float overlap = 0.0;
    float planeValue = 0.0;
    float3 planeGradient = float3(0.0, 0.0, 0.0);
    uint phase = (surfaceShown ? 0u : (planeShown ? 1u : 2u));
    uint taps = (gradient ? 4u : 1u);
    uint tapIndex = 0u;
    uint marched = 0u;
    float marchT = 0.0;
    float minimumStep = max((0.5 * tolerance), 0.002);

    [loop]
    for (uint step = 0u; ((step < SDF_FIELD_MAX_QUERIES) && (phase < 2u)); step++) {
        float3 tapDirection = float3((((tapIndex == 0u) || (tapIndex == 3u)) ? 1.0 : -1.0), (((tapIndex == 0u) || (tapIndex == 1u)) ? -1.0 : 1.0), (((tapIndex == 1u) || (tapIndex == 3u)) ? 1.0 : -1.0));
        float3 query = ((phase == 0u) ? (surfacePoint + (direction * marchT)) : (planePoint + ((taps > 1u) ? (tapDirection * tap) : float3(0.0, 0.0, 0.0))));
        float ownDistance;
        float otherDistance;

        sdfFieldQuery(query, ((phase == 0u) ? own : SdfFieldNoObject), ((phase == 0u) && (passGroup.debugOptions.y <= 0.0)), overflow, ownDistance, otherDistance);
        if (phase == 0u) {
            float intersection = max(ownDistance, otherDistance);
            bool done = false;

            if (marched == 0u) {
                // The visible surface: inside another object only where the meshes disagree with the field.
                surfaceOther = otherDistance;
                overlap = max(overlap, -otherDistance);
                done = !(otherDistance < proximityReach);
            } else {
                overlap = max(overlap, -intersection);
                // Past the surface's own object, nothing further along overlaps it.
                done = ((ownDistance > (tolerance + 0.01)) || (marchT > (2.0 * proximityReach)));
            }
            marched++;
            marchT += max(minimumStep, abs((marched == 1u) ? otherDistance : intersection));
            if (done || (marched > SDF_FIELD_MARCH_STEPS) || (overlap > (tolerance + SDF_FIELD_HOT_DEPTH))) {
                phase = (planeShown ? 1u : 2u);
            }
        } else {
            planeValue += (otherDistance / (float)taps);
            planeGradient += (tapDirection * otherDistance);
            tapIndex++;
            if (tapIndex >= taps) {
                phase = 2u;
            }
        }
    }

    float planeMagnitude = (length(planeGradient) / (4.0 * tap));
    uint tileIndex = ((groupLocal.y * SDF_FIELD_TILE_EDGE) + groupLocal.x);

    sdfFieldTilePlane[tileIndex] = ((planeShown && (planeValue < SDF_FAR_DISTANCE)) ? planeValue : asfloat(0x7FC00000u));
    sdfFieldTileProximity[tileIndex] = ((surfaceShown && (surfaceOther < SDF_FAR_DISTANCE)) ? surfaceOther : asfloat(0x7FC00000u));
    GroupMemoryBarrierWithGroupSync();

    if (inside) {
        float4 layer = float4(0.0, 0.0, 0.0, 0.0);

        if (surfaceShown) {
            if (overlap > tolerance) {
                float3 hot = lerp(float3(1.0, 0.05, 0.8), float3(1.0, 0.9, 0.1), sqrt(saturate((overlap - tolerance) / SDF_FIELD_HOT_DEPTH)));

                layer = sdfFieldOver(layer, hot, 0.95);
            } else if (surfaceOther < proximityReach) {
                float width = max(sdfFieldPixelWidth(sdfFieldTileProximity[tileIndex], sdfFieldTileProximity[sdfFieldAcross(groupLocal.xy)],
                    sdfFieldTileProximity[sdfFieldDown(groupLocal.xy)], (footprint * surface)), tolerance);
                float3 rings = sdfFieldDistanceColour(max(surfaceOther, 0.0), proximityBand, width);
                float fadeOut = (1.0 - smoothstep((0.6 * proximityReach), proximityReach, surfaceOther));

                layer = sdfFieldOver(layer, rings, (SDF_FIELD_PROXIMITY_OPACITY * fadeOut));
            }
        }
        if (planeShown && (planeValue < SDF_FAR_DISTANCE)) {
            float width = sdfFieldPixelWidth(sdfFieldTilePlane[tileIndex], sdfFieldTilePlane[sdfFieldAcross(groupLocal.xy)],
                sdfFieldTilePlane[sdfFieldDown(groupLocal.xy)], (footprint * planeT));
            float3 colour = (gradient ? sdfFieldGradientColour(planeValue, planeMagnitude, width) : sdfFieldDistanceColour(planeValue, band, width));
            float opacity = (saturate(passGroup.fieldStyle.y) * (((surface > 0.0) && (surface < planeT)) ? SDF_FIELD_HIDDEN : 1.0));

            layer = sdfFieldOver(layer, colour, opacity);
        }

        float3 straight = ((layer.a > 0.0) ? (layer.rgb / layer.a) : float3(0.0, 0.0, 0.0));
        uint3 encoded = (uint3)round(saturate(puckLinearToSrgbExtended(saturate(straight))) * 255.0);

        sdfFieldColorsRW[(pixel.y * extent.x) + pixel.x] = (encoded.x | (encoded.y << 8u) | (encoded.z << 16u) | ((uint)round(saturate(layer.a) * 255.0) << 24u));
        sdfWorkTexels = 1u;
    }
    puckCountWork(sdfWorkSteps, sdfWorkTexels);
    puckCountShapes(sdfWorkShapes, sdfWorkGradients);
}
