#ifndef PUCK_SDF_PART_BOUNDS_HLSLI
#define PUCK_SDF_PART_BOUNDS_HLSLI

// Two cache bands follow the four tile planes: primary acceptance bounds, then AO sublevel bounds. Each band
// stores two float3 corners per (viewport, live instance). The beam writes both before its compute barrier.
// KEEP IN SYNC with SdfWorldEngine.PartBoundFloatCount and its construction-capacity tile allocation.
static const uint SdfPartBoundFloatCount = 12u;
static const uint SdfBoundCornerFloatCount = 6u;
static const float SdfContactFieldLevel = 0.15;
static uint sdfPartBoundsViewport = 0u;

uint sdfPartBoundIndex(uint viewport, uint instance) {
    uint tileCount = params.tileGrid.x * params.tileGrid.y * params.viewportCount;
    return WorldTilePlaneCount * tileCount + SdfBoundCornerFloatCount *
        (viewport * sdfProgramLayout.instanceCount + instance);
}

uint sdfContactBoundIndex(uint viewport, uint instance) {
    return sdfPartBoundIndex(viewport, instance) + SdfBoundCornerFloatCount *
        params.viewportCount * sdfProgramLayout.instanceCount;
}

// Enclose a primitive's positive sublevel set, not merely its zero surface: primary accepts a band whose
// width depends on pixel footprint and the field's distance corrections. Unknown formulae decline the cache.
bool sdfPartShapeBox(uint type, float4 d0, float4 d1, float level, out float3 box) {
    box = 0.0;
    if (type == SDF_SHAPE_SPHERE) {
        box = abs(d0.x) + level;
    } else if (type == SDF_SHAPE_CAPSULE) {
        box = abs(d0.xyz) + abs(d0.w) + level;
    } else if (type == SDF_SHAPE_TORUS) {
        box = float3(abs(d0.x) + abs(d0.y), abs(d0.y), abs(d0.x) + abs(d0.y)) + level;
    } else if (type == SDF_SHAPE_ELLIPSOID) {
        if (any(d1.yzw <= 0.0) || min(d1.y, min(d1.z, d1.w)) < SDF_ELLIPSOID_MIN_DENOM) return false;
        float minRadius = 1.0 / max(d1.y, max(d1.z, d1.w));
        box = (1.0 + level / minRadius) / d1.yzw;
    } else if (type == SDF_SHAPE_SWEEP) {
        float3 a, b, c;
        float r0, r1, bulge;
        sdfSweepCurve(asuint(d0.x), a, b, c, r0, r1, bulge);
        float margin = max(sdfSweepConservativeMargin(bulge, d0.w, d0.z, r0, r1), 0.0);
        // The evaluator caps its running minimum at FAR before subtracting the margin.
        if (level + margin >= SDF_FAR_DISTANCE * 0.5) return false;
        float radius = max(abs(r0), abs(r1)) + abs(bulge) + 2.0 * abs(d0.w) + margin + level;
        box = max(abs(a), max(abs(b), abs(c))) + radius;
    } else if (type == SDF_SHAPE_SUPERELLIPSOID) {
        float minRadius = min(d0.x, min(d0.y, d0.z));
        if (minRadius <= 0.0 || any(d1.yzw <= 0.0) || d0.w < 2.0) return false;
        // The Lp norm is at least each absolute coordinate. Its field scale is the minimum radius.
        box = (1.0 + level / minRadius) / d1.yzw;
    } else if (type == SDF_SHAPE_BOX || type == SDF_SHAPE_SCREEN_SLAB) {
        box = abs(d0.xyz) + level + abs(d0.w);
    } else if (type == SDF_SHAPE_CYLINDER) {
        box = float3(abs(d0.x), abs(d0.y), abs(d0.x)) + level + abs(d1.w);
    } else if (type == SDF_SHAPE_ELLIPSE || type == SDF_SHAPE_CONVEX_POLYGON || type == SDF_SHAPE_ROUNDED_RECT) {
        float2 profile = abs(d0.xy);
        if (type == SDF_SHAPE_CONVEX_POLYGON) {
            uint packed = asuint(d0.x), count = packed & 15u, offset = packed >> 4u;
            if (count < 3u) return false;
            profile = 0.0;
            [loop] for (uint vertex = 0u; vertex < count; vertex++)
                profile = max(profile, abs(sdfPolygonVertex(offset, vertex)));
        }
        // Cap chamfer removes material; edge rounding can expand the profile and cap by this margin.
        float margin = level + abs(d1.w);
        if (d1.y > 0.5) box = float3(profile, abs(d0.w)) + margin;
        else box = float3(abs(d0.w) + profile.x + margin, profile.y + margin,
            abs(d0.w) + profile.x + margin);
    } else return false;
    return all(isfinite(box)) && all(box >= 0.0);
}

bool sdfPartLeafBox(uint shapeIndex, uint domainCode, uint poseBinding, float level, out float3 lower, out float3 upper) {
    lower = 1e20; upper = -1e20;
    uint4 shape = sdfWords[1u + shapeIndex];
    if (!sdfShapeEnabled(shape.y)) return true;
    uint4 domain = 0u;
    float4 domain0 = 0.0, domain1 = 0.0;
    float correction = 1.0;
    if (domainCode != 0u) {
        uint domainIndex = domainCode - 1u;
        domain = sdfWords[1u + domainIndex];
        domain0 = asfloat(sdfWords[sdfProgramLayout.dataOffset + 2u * domainIndex]);
        domain1 = asfloat(sdfWords[sdfProgramLayout.dataOffset + 2u * domainIndex + 1u]);
        if (domain.x == SDF_OP_SCALE) correction = domain0.w;
        else if (domain.x == SDF_OP_AXIAL_PROFILE) correction = domain1.x;
        else if (domain.x != SDF_OP_SHEAR) return false;
        if (!(correction > 0.0) || !isfinite(correction)) return false;
    }
    float4 shape0 = asfloat(sdfWords[sdfProgramLayout.dataOffset + 2u * shapeIndex]);
    float4 shape1 = asfloat(sdfWords[sdfProgramLayout.dataOffset + 2u * shapeIndex + 1u]);
    float3 box;
    if (!sdfPartShapeBox(shape.y & SDF_SHAPE_TYPE_MASK, shape0, shape1, level / correction, box)) return false;
    if (domainCode != 0u) {
        if (domain.x == SDF_OP_SCALE) box *= abs(domain0.xyz);
        else if (domain.x == SDF_OP_AXIAL_PROFILE) {
            float maximumScale = max(SDF_FLARE_MIN_SCALE, abs(domain1.y) + abs(domain0.x) + abs(domain0.y));
            [unroll] for (uint component = 0u; component < 3u; component++)
                if (component != domain.y) box[component] *= maximumScale;
        } else {
            float reach = box[domain.z];
            box[domain.y] += abs(domain0.x) * reach + abs(domain0.y) * reach * reach +
                abs(domain0.z) * reach * reach * reach;
        }
    }
    float3 center = 0.0;

    if (poseBinding != 0u) {
        uint pose = poseBinding - 1u;
        center = sdfDynamicTransforms[3u * pose].xyz;
        float4 q = sdfDynamicTransforms[3u * pose + 1u];
        box = abs(rotatePointByQuaternion(float3(box.x, 0, 0), q)) +
            abs(rotatePointByQuaternion(float3(0, box.y, 0), q)) +
            abs(rotatePointByQuaternion(float3(0, 0, box.z), q));
    }
    box += max(0.001, 0.00001 * (1.0 + length(center) + length(box)));
    if (!all(isfinite(box)) || !all(isfinite(center))) return false;
    lower = center - box;
    upper = center + box;
    return true;
}

bool sdfChainSublevelBox(uint cursor, uint end, float level, bool requireUnion, out float3 lower, out float3 upper) {
    lower = -1e20; upper = 1e20;
    if (cursor >= end || sdfWords[1u + cursor++].x != SDF_OP_RESET) return false;
    uint poseBinding = 0u, domainCode = 0u;
    if (cursor < end && sdfWords[1u + cursor].x == SDF_OP_TRANSFORM_DYNAMIC) {
        poseBinding = (uint)asfloat(sdfWords[sdfProgramLayout.dataOffset + 2u * cursor]).x + 1u;
        cursor++;
    }
    float3 translation = 0.0;
    float4 rotation = float4(0, 0, 0, 1);
    if (cursor < end && sdfWords[1u + cursor].x == SDF_OP_TRANSLATE) {
        translation = asfloat(sdfWords[sdfProgramLayout.dataOffset + 2u * cursor]).xyz;
        cursor++;
    }
    if (cursor < end && sdfWords[1u + cursor].x == SDF_OP_ROTATE) {
        rotation = asfloat(sdfWords[sdfProgramLayout.dataOffset + 2u * cursor]);
        cursor++;
    }
    if (cursor < end) {
        uint op = sdfWords[1u + cursor].x;
        if (op == SDF_OP_SCALE || op == SDF_OP_AXIAL_PROFILE || op == SDF_OP_SHEAR) domainCode = ++cursor;
    }
    if (cursor + 1u != end) return false;
    uint4 shape = sdfWords[1u + cursor];
    if (shape.x != SDF_OP_SHAPE || (requireUnion && shape.z != SDF_BLEND_UNION)) return false;
    if (!sdfPartLeafBox(cursor, domainCode, 0u, level, lower, upper)) return false;
    if (any(lower > upper)) return true;
    float3 center = (lower + upper) * 0.5, box = (upper - lower) * 0.5;
    center = rotatePointByQuaternion(center, rotation) + translation;
    box = abs(rotatePointByQuaternion(float3(box.x, 0, 0), rotation)) +
          abs(rotatePointByQuaternion(float3(0, box.y, 0), rotation)) +
          abs(rotatePointByQuaternion(float3(0, 0, box.z), rotation));
    if (poseBinding != 0u) {
        uint pose = poseBinding - 1u;
        float4 q = sdfDynamicTransforms[3u * pose + 1u];
        center = rotatePointByQuaternion(center, q) + sdfDynamicTransforms[3u * pose].xyz;
        box = abs(rotatePointByQuaternion(float3(box.x, 0, 0), q)) +
              abs(rotatePointByQuaternion(float3(0, box.y, 0), q)) +
              abs(rotatePointByQuaternion(float3(0, 0, box.z), q));
    }
    box += max(0.001, 0.00001 * (1.0 + length(center) + length(box)));
    lower = center - box; upper = center + box;
    return all(isfinite(lower)) && all(isfinite(upper));
}

bool sdfFlatSublevelBox(uint instance, float level, out float3 lower, out float3 upper) {
    lower = -1e20; upper = 1e20;
    uint4 meta = sdfWords[sdfInstanceEntryOffset(sdfProgramLayout.instanceOffset, instance) + 1u];
    uint segmentEnd = meta.w & SDF_INSTANCE_SEGMENT_END_MASK;
    if (segmentEnd <= meta.z) return false;
    uint first = sdfWords[sdfProgramLayout.segmentOffset + 2u + 2u * meta.z].z;
    uint end = sdfWords[sdfProgramLayout.segmentOffset + 2u + 2u * (segmentEnd - 1u)].w;
    if (segmentEnd == meta.z + 1u && sdfChainSublevelBox(first, end, level, true, lower, upper)) return true;
    if (first >= end || sdfWords[1u + first].x != SDF_OP_PUSH_FIELD) return false;
    uint4 pop = sdfWords[end];
    if (pop.x != SDF_OP_POP_FIELD || pop.z != SDF_BLEND_UNION) return false;
    float scale = asfloat(sdfWords[sdfProgramLayout.dataOffset + 2u * (end - 1u) + 1u]).y;
    if (scale <= 0.0) scale = 1.0;
    level /= scale;
    float slack = 0.0;
    [loop] for (uint instruction = first + 1u; instruction < end - 1u; instruction++) {
        uint4 code = sdfWords[1u + instruction];
        if (code.x != SDF_OP_SHAPE || !sdfShapeEnabled(code.y)) continue;
        if (code.z == SDF_BLEND_SMOOTH_UNION) slack += max(asfloat(sdfWords[sdfProgramLayout.dataOffset + 2u * instruction + 1u]).x, SDF_SMOOTH_RADIUS_MIN) * 0.25;
        else if (!(code.z == SDF_BLEND_UNION || code.z == SDF_BLEND_SUBTRACTION || code.z == SDF_BLEND_INTERSECTION || code.z == SDF_BLEND_SMOOTH_INTERSECTION || code.z == SDF_BLEND_SMOOTH_SUBTRACTION)) return false;
    }
    lower = 1e20; upper = -1e20;
    uint cursor = first + 1u;
    [loop] while (cursor < end - 1u) {
        uint chainEnd = cursor + 1u;
        [loop] while (chainEnd < end - 1u && sdfWords[1u + chainEnd].x != SDF_OP_RESET) chainEnd++;
        float3 chainLower, chainUpper;
        if (!sdfChainSublevelBox(cursor, chainEnd, level + slack, false, chainLower, chainUpper)) return false;
        lower = min(lower, chainLower); upper = max(upper, chainUpper);
        cursor = chainEnd;
    }
    return true;
}

bool sdfPartSublevelBox(uint4 part, float depth, float footprint, float minimumLevel, out float3 lower, out float3 upper) {
    lower = 1e20;
    upper = -1e20;
    float scopeScale = asfloat(part.w) * sdfProgramLayout.stepScale;
    if (!(scopeScale > 0.0) || !isfinite(scopeScale)) return false;
    float level = max(max(SurfaceEpsilon, footprint * depth), minimumLevel * sdfProgramLayout.stepScale) / scopeScale;
    uint count = part.z & 0x7FFFFFFFu;
    float blendSlack = 0.0;
    [loop] for (uint leaf = 0u; leaf < count; leaf++) {
        uint4 code = sdfWords[part.x + leaf];
        uint4 shape = sdfWords[1u + code.x];
        if (!sdfShapeEnabled(shape.y)) continue;
        if (shape.z == SDF_BLEND_SMOOTH_UNION) {
            float k = asfloat(sdfWords[sdfProgramLayout.dataOffset + 2u * code.x + 1u]).x;
            // Polynomial smooth-min lowers min(a,b) by at most k/4. Sum the whole ordered chain's slack.
            blendSlack += max(k, SDF_SMOOTH_RADIUS_MIN) * 0.25;
        } else if (!(shape.z == SDF_BLEND_UNION || shape.z == SDF_BLEND_SUBTRACTION ||
            shape.z == SDF_BLEND_INTERSECTION || shape.z == SDF_BLEND_SMOOTH_INTERSECTION ||
            shape.z == SDF_BLEND_SMOOTH_SUBTRACTION)) return false;
    }
    level += blendSlack;
    if (!isfinite(level) || level >= SDF_FAR_DISTANCE * 0.5) return false;

    // Enclosing every leaf is conservative for union. The admitted subtraction/intersection operations
    // can only raise the accumulated distance; including their candidates may loosen, never shrink, this box.
    [loop] for (uint leaf = 0u; leaf < count; leaf++) {
        uint4 code = sdfWords[part.x + leaf];
        uint4 shape = sdfWords[1u + code.x];
        if (!sdfShapeEnabled(shape.y)) continue;
        float3 leafLower, leafUpper;
        if (!sdfPartLeafBox(code.x, code.y, sdfWords[part.y + leaf].x, level, leafLower, leafUpper)) return false;
        lower = min(lower, leafLower); upper = max(upper, leafUpper);
    }
    return true;
}

void sdfWritePartBound(uint viewport, uint instance, float3 origin, float farDistance, float footprint) {
    uint4 part = sdfWords[sdfProgramLayout.partProgramOffset + 1u + instance];
    // Unbounded sentinel declines clipping; reversed corners describe an empty part.
    float3 lower = -1e20, upper = 1e20;
    if ((part.z & 0x7FFFFFFFu) != 0u) {
        float depth = farDistance;
        [loop] for (uint refinement = 0u; refinement < 3u; refinement++) {
            float3 nextLower, nextUpper;
            if (!sdfPartSublevelBox(part, depth, footprint, 0.0, nextLower, nextUpper)) {
                lower = -1e20; upper = 1e20;
                break;
            }
            lower = nextLower; upper = nextUpper;
            // Every accepted point is inside this box. Its farthest corner therefore gives another conservative
            // depth limit for the next sublevel bound, reducing the far plane's overly broad footprint padding.
            depth = min(depth, length(max(abs(lower - origin), abs(upper - origin))));
        }
    }
    uint base = sdfPartBoundIndex(viewport, instance);
    tiles[base] = lower.x; tiles[base + 1u] = lower.y; tiles[base + 2u] = lower.z;
    tiles[base + 3u] = upper.x; tiles[base + 4u] = upper.y; tiles[base + 5u] = upper.z;
    bool contactBounded;
    if ((part.z & 0x7FFFFFFFu) != 0u)
        contactBounded = sdfPartSublevelBox(part, 0.0, 0.0, SdfContactFieldLevel, lower, upper);
    else contactBounded = sdfFlatSublevelBox(instance, SdfContactFieldLevel, lower, upper);
    if (!contactBounded) { lower = -1e20; upper = 1e20; }
    base = sdfContactBoundIndex(viewport, instance);
    tiles[base] = lower.x; tiles[base + 1u] = lower.y; tiles[base + 2u] = lower.z;
    tiles[base + 3u] = upper.x; tiles[base + 4u] = upper.y; tiles[base + 5u] = upper.z;
}

#if defined(SDF_PRIMARY_READ)
bool sdfInstanceOutsideContactBox(uint instance, float3 low, float3 high) {
    uint base = sdfContactBoundIndex(sdfPartBoundsViewport, instance);
    float3 lower = float3(tiles[base], tiles[base + 1u], tiles[base + 2u]);
    float3 upper = float3(tiles[base + 3u], tiles[base + 4u], tiles[base + 5u]);
    return any(high < lower) || any(low > upper);
}
bool sdfPartCannotImprove(uint instance, float3 p, float distance) {
    if (distance > SdfContactFieldLevel || sdfDetailShadingActive || !sdfCanTracePartsIndependently()) return false;
    return sdfInstanceOutsideContactBox(instance, p, p);
}
#endif

bool sdfPartRayInterval(uint instance, float3 origin, float3 direction, inout float entry, inout float exit) {
    uint base = sdfPartBoundIndex(sdfPartBoundsViewport, instance);
    float3 lower = float3(tiles[base], tiles[base + 1u], tiles[base + 2u]);
    float3 upper = float3(tiles[base + 3u], tiles[base + 4u], tiles[base + 5u]);
    if (lower.x <= -1e19) return true;
    if (any(lower > upper)) return false;
    [unroll] for (uint axis = 0u; axis < 3u; axis++) {
        // Parallel rays on a slab face must avoid 0 * infinity and retain the boundary.
        if (abs(direction[axis]) < 1e-20) {
            if (origin[axis] < lower[axis] || origin[axis] > upper[axis]) return false;
        } else {
            float a = (lower[axis] - origin[axis]) / direction[axis];
            float b = (upper[axis] - origin[axis]) / direction[axis];
            entry = max(entry, min(a, b)); exit = min(exit, max(a, b));
        }
    }
    return entry <= exit;
}

#endif
