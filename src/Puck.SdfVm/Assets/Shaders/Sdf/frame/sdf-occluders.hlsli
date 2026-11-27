// The analytic occluders (sdfOccluders, SdfOccluder records generated from Puck.SignedDistance.SdfOccluder): a sphere,
// capsule or box standing in for a creation, which takes away the share of a surface's ambient and indirect light its
// covered hemisphere holds and casts a soft shadow toward a ranged light, a screen or an emitter face. A view walks the
// occluders of its mask (the pass block's occluderMask, SdfLightSelection), which a views tile narrows to those reaching
// its hit points (sdfViewsTileLights) and glass to those reaching its wave's (sdfWaveLightMasks), by the set bits, as it
// walks lights; a view whose mask is empty reads no record.
//
// The invariant every mask rests on: an occluder adds nothing at a point Reach or farther from its center. Both its
// terms are scaled by the window a point light's falloff eases to zero by (sdfLightWindow over the distance to the
// center against Reach), so a mask may leave out an occluder whose sphere of reach misses every point its lanes shade
// (sdfOccluderReachesBox), and nothing else.
//
// A surface of the moving body an occluder stands for (its frame slot within OwnSlot..OwnSlot + OwnSlots) is never
// darkened or shadowed by that occluder, which only approximates the body's own shape.
//
// Presentation float: nothing here reads the field or feeds the simulation.
#ifndef FRAME_SDF_OCCLUDERS_HLSLI
#define FRAME_SDF_OCCLUDERS_HLSLI
#include "sdf-lights.hlsli"

// Both the declared count and the actual bound buffer constrain a read.
uint worldOccluderCount() {
    uint count;
    uint stride;
    sdfOccluders.GetDimensions(count, stride);
    return min(passGroup.occluderCount, min(count, SDF_MAX_OCCLUDERS));
}

// Whether an occluder can add anything at a point of the box `low`..`high`: whether the box meets its sphere of reach.
bool sdfOccluderReachesBox(SdfOccluder occluder, float3 low, float3 high) {
    float reach = (occluder.Reach * (1.0 + SdfLightReachMargin));

    return (sdfBoxDistanceSquared(occluder.Center, low, high) <= (reach * reach));
}
// Whether a surface riding `frameSlot` is the occluder's own body's.
bool sdfOccluderOwns(SdfOccluder occluder, int frameSlot) {
    return ((occluder.OwnSlots != 0u) && (frameSlot != SDF_TRANSFORM_SLOT_NONE) && (((uint)(frameSlot - occluder.OwnSlot)) < occluder.OwnSlots));
}
// The occluder's window at a point, one at its center and exactly zero at and past its reach (sdfLightWindow).
float sdfOccluderWindow(SdfOccluder occluder, float3 position) {
    float3 offset = (position - occluder.Center);

    return sdfLightWindow(dot(offset, offset), occluder.Reach);
}

// The share of the cosine-weighted hemisphere about `normal` at `position` that a sphere covers: its form factor, as a
// sphere light's illuminance over pi (Quilez, "sphere ambient occlusion"; where the horizon cuts the sphere, Lagarde and
// de Rousiers, "Moving Frostbite to Physically Based Rendering", 2014, eq. 31). A point inside the sphere answers as one
// on its surface, so the answer is continuous across it.
float sdfSphereOcclusion(float3 position, float3 normal, float3 center, float radius) {
    float3 offset = (center - position);
    float distance = length(offset);
    float cosine = clamp((dot(normal, offset) / max(distance, 1.0e-6)), -1.0, 1.0);
    // The sphere's angular radius's sine squared, at most just under one.
    float sineSquared = min(((radius * radius) / max((distance * distance), 1.0e-12)), 0.9999);

    if ((cosine * cosine) > sineSquared) {
        return saturate(sineSquared * max(cosine, 0.0));
    }

    float sine = sqrt(max((1.0 - (cosine * cosine)), 1.0e-6));
    float x = sqrt((1.0 / sineSquared) - 1.0);
    float y = clamp((-x * (cosine / sine)), -1.0, 1.0);
    float sineRoot = (sine * sqrt(1.0 - (y * y)));

    return saturate(((((cosine * acos(y)) - (x * sineRoot)) * sineSquared) + atan(sineRoot / x)) / SDF_PI);
}
// The point of a capsule's axis, from `center` less `halfSegment` to it plus `halfSegment`, nearest `position`.
float3 sdfOccluderAxisNearest(float3 center, float3 halfSegment, float3 position) {
    float halfSquared = dot(halfSegment, halfSegment);
    float along = ((halfSquared > 1.0e-12) ? clamp((dot((position - center), halfSegment) / halfSquared), -1.0, 1.0) : 0.0);

    return (center + (halfSegment * along));
}
// One edge's term of Lambert's polygon formula: the angle the edge from unit direction `from` to `to` subtends, times
// the normal's share of the unit normal of the plane through both; nothing for an edge seen end on.
float sdfPolygonEdge(float3 from, float3 to, float3 normal) {
    float3 plane = cross(from, to);
    float planeLength = sqrt(dot(plane, plane));

    return ((planeLength > 1.0e-6) ? (acos(clamp(dot(from, to), -1.0, 1.0)) * (dot(normal, plane) / planeLength)) : 0.0);
}
// The form factor of a box's face along its first axis seen from `p` (in the box's frame, its axes cyclically permuted
// so that face's axis comes first, which keeps the frame right-handed), unclipped by the horizon: Lambert's polygon
// formula, the sum of the edges' terms (sdfPolygonEdge) over 2 pi (Quilez, "box occlusion"). The corners run so a face
// above the horizon gives a positive sum; a face whose slab holds the point shows only its edge and gives nothing.
float sdfBoxFaceOcclusion(float3 p, float3 n, float3 extent) {
    if (abs(p.x) <= extent.x) {
        return 0.0;
    }

    float side = ((p.x > 0.0) ? 1.0 : -1.0);
    float3 c0 = normalize(float3((side * extent.x), extent.y, extent.z) - p);
    float3 c1 = normalize(float3((side * extent.x), -extent.y, extent.z) - p);
    float3 c2 = normalize(float3((side * extent.x), -extent.y, -extent.z) - p);
    float3 c3 = normalize(float3((side * extent.x), extent.y, -extent.z) - p);
    float sum = (((sdfPolygonEdge(c0, c1, n) + sdfPolygonEdge(c1, c2, n)) + sdfPolygonEdge(c2, c3, n)) + sdfPolygonEdge(c3, c0, n));

    return ((-side * sum) / (2.0 * SDF_PI));
}
// A box occluder's half extents along its axes, and a point's and a direction's coordinates along its unit axes about its
// center, in world units.
float3 sdfOccluderBoxExtent(SdfOccluder occluder) {
    return float3(length(occluder.AxisX), length(occluder.AxisY), length(occluder.AxisZ));
}
float3 sdfOccluderBoxDirection(SdfOccluder occluder, float3 extent, float3 direction) {
    return (float3(dot(direction, occluder.AxisX), dot(direction, occluder.AxisY), dot(direction, occluder.AxisZ)) / max(extent, 1.0e-6));
}
float3 sdfOccluderBoxPoint(SdfOccluder occluder, float3 extent, float3 position) {
    return sdfOccluderBoxDirection(occluder, extent, (position - occluder.Center));
}
// The share of the hemisphere about `normal` at `position` an occluder covers, before its strength and window.
float sdfOccluderCoverage(SdfOccluder occluder, float3 position, float3 normal) {
    if (occluder.Shape == SDF_OCCLUDER_BOX) {
        float3 extent = sdfOccluderBoxExtent(occluder);
        float3 p = sdfOccluderBoxPoint(occluder, extent, position);
        float3 n = sdfOccluderBoxDirection(occluder, extent, normal);

        return saturate((sdfBoxFaceOcclusion(p, n, extent) + sdfBoxFaceOcclusion(p.yzx, n.yzx, extent.yzx)) + sdfBoxFaceOcclusion(p.zxy, n.zxy, extent.zxy));
    }

    float3 center = ((occluder.Shape == SDF_OCCLUDER_CAPSULE) ? sdfOccluderAxisNearest(occluder.Center, occluder.AxisX, position) : occluder.Center);

    return sdfSphereOcclusion(position, normal, center, occluder.Radius);
}
// What reaches a surface of the ambient and indirect light the occluders of `mask` leave it: the product over them of
// one less each's strength times its window times the share of the hemisphere it covers (sdfOccluderCoverage). One where
// the mask holds none.
float sdfOccludersAmbient(uint4 mask, float3 position, float3 normal, int frameSlot) {
    float ambient = 1.0;
    uint count = worldOccluderCount();

    [loop] for (uint word = 0u; (word < 4u); word++) {
        uint bits = sdfComponent(mask, word);

        [loop] while (bits != 0u) {
            uint index = ((word * 32u) + firstbitlow(bits));

            bits &= (bits - 1u);
            if (index >= count) {
                break;
            }

            SdfOccluder occluder = sdfOccluders[index];

            if (sdfOccluderOwns(occluder, frameSlot)) {
                continue;
            }

            float window = sdfOccluderWindow(occluder, position);

            if ((window > 0.0) && (occluder.Strength > 0.0)) {
                ambient *= (1.0 - saturate((occluder.Strength * window) * sdfOccluderCoverage(occluder, position, normal)));
            }
        }
    }

    return ambient;
}

// The signed distance from a point to a box of half extents `extent` about the origin.
float sdfOccluderBoxDistance(float3 p, float3 extent) {
    float3 q = (abs(p) - extent);

    return (length(max(q, 0.0)) + min(max(q.x, max(q.y, q.z)), 0.0));
}
// The golden section's ratio, which a box's gap search narrows its interval by at each step.
static const float SdfOccluderGolden = 0.6180339887;
// The least signed distance from the ray segment `origin` + t `direction` (t in 0..far, `direction` a unit vector) to an
// occluder's solid, in x, and the ray parameter where it is least, in y: a sphere's and a capsule's in closed form
// (Quilez, "sphere soft shadow" and "capsule soft shadow"), a box's by a golden-section search over the part of the
// segment within its bounding sphere, along which a box's distance is convex.
float2 sdfOccluderRayGap(SdfOccluder occluder, float3 origin, float3 direction, float far) {
    if (occluder.Shape == SDF_OCCLUDER_BOX) {
        float3 extent = sdfOccluderBoxExtent(occluder);
        float bound = length(extent);
        float middle = dot((occluder.Center - origin), direction);
        float low = clamp((middle - bound), 0.0, far);
        float high = clamp((middle + bound), 0.0, far);
        float3 p = sdfOccluderBoxPoint(occluder, extent, origin);
        float3 d = sdfOccluderBoxDirection(occluder, extent, direction);
        float a = (high - (SdfOccluderGolden * (high - low)));
        float b = (low + (SdfOccluderGolden * (high - low)));
        float atA = sdfOccluderBoxDistance((p + (d * a)), extent);
        float atB = sdfOccluderBoxDistance((p + (d * b)), extent);

        [loop] for (uint step = 0u; (step < 8u); step++) {
            if (atA < atB) {
                high = b;
                b = a;
                atB = atA;
                a = (high - (SdfOccluderGolden * (high - low)));
                atA = sdfOccluderBoxDistance((p + (d * a)), extent);
            } else {
                low = a;
                a = b;
                atA = atB;
                b = (low + (SdfOccluderGolden * (high - low)));
                atB = sdfOccluderBoxDistance((p + (d * b)), extent);
            }
        }

        float t = (0.5 * (low + high));

        return float2(sdfOccluderBoxDistance((p + (d * t)), extent), t);
    }
    if (occluder.Shape == SDF_OCCLUDER_CAPSULE) {
        float3 start = (occluder.Center - occluder.AxisX);
        float3 span = (2.0 * occluder.AxisX);
        float spanSquared = dot(span, span);
        float3 fromStart = (origin - start);
        float rayAlong = dot(fromStart, direction);
        float spanAlong = dot(direction, span);
        float spanFrom = dot(fromStart, span);
        float denominator = (spanSquared - (spanAlong * spanAlong));
        // The two lines' nearest points, then each clamped to its segment and the ray's taken again from the axis's.
        float along = ((denominator > (1.0e-6 * max(spanSquared, 1.0e-12))) ? saturate((spanFrom - (rayAlong * spanAlong)) / denominator)
            : ((spanSquared > 1.0e-12) ? saturate(spanFrom / spanSquared) : 0.0));
        float t = clamp(dot(((start + (span * along)) - origin), direction), 0.0, far);

        along = ((spanSquared > 1.0e-12) ? saturate(dot(((origin + (direction * t)) - start), span) / spanSquared) : 0.0);

        return float2((length((origin + (direction * t)) - (start + (span * along))) - occluder.Radius), t);
    }

    float t = clamp(dot((occluder.Center - origin), direction), 0.0, far);

    return float2((length((origin + (direction * t)) - occluder.Center) - occluder.Radius), t);
}
// The share of a light the occluders of `mask` leave a surface at `position`, the light at `target`, its radius (the
// half size of the disc it is taken as) `lightRadius`: for each occluder, the light's visible share across the occluder's
// silhouette, smoothstepped over a penumbra centred on it whose half width at a distance t along the ray is t times the
// wider of the light's half slope from the surface (its radius over its distance) and the occluder's Softness, scaled by
// its Shadow strength and its window, multiplied together. One where the mask holds none.
float sdfOccludersShadow(uint4 mask, float3 position, float3 target, float lightRadius, int frameSlot) {
    float visible = 1.0;
    float3 toLight = (target - position);
    float far = length(toLight);

    if (far <= 1.0e-4) {
        return 1.0;
    }

    float3 direction = (toLight / far);
    float lightSlope = (lightRadius / far);
    uint count = worldOccluderCount();

    [loop] for (uint word = 0u; (word < 4u); word++) {
        uint bits = sdfComponent(mask, word);

        [loop] while (bits != 0u) {
            uint index = ((word * 32u) + firstbitlow(bits));

            bits &= (bits - 1u);
            if (index >= count) {
                break;
            }

            SdfOccluder occluder = sdfOccluders[index];

            if ((occluder.Shadow <= 0.0) || sdfOccluderOwns(occluder, frameSlot)) {
                continue;
            }

            float window = sdfOccluderWindow(occluder, position);

            if (window <= 0.0) {
                continue;
            }

            float2 gap = sdfOccluderRayGap(occluder, position, direction, far);
            float width = max((gap.y * max(lightSlope, occluder.Softness)), 1.0e-4);
            float share = saturate(0.5 + ((0.5 * gap.x) / width));

            visible *= (1.0 - ((occluder.Shadow * window) * (1.0 - ((share * share) * (3.0 - (2.0 * share))))));
        }
    }

    return visible;
}
#endif
