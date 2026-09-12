#ifndef PUCK_SDF_PART_BOUNDS_HLSLI
#define PUCK_SDF_PART_BOUNDS_HLSLI

// Presentation cache for independent whole-part tracing. The beam writes two float3 world-space corners
// per (viewport, instance), after the four tile planes; primary reads after the beam's compute barrier.
// KEEP IN SYNC with SdfWorldEngine.PartBoundFloatCount and its construction-capacity tile allocation.
static const uint SdfPartBoundFloatCount = 6u;
static uint sdfPartBoundsViewport = 0u;

uint sdfPartBoundIndex(uint viewport, uint instance) {
    uint tileCount = params.tileGrid.x * params.tileGrid.y * params.viewportCount;
    return WorldTilePlaneCount * tileCount + SdfPartBoundFloatCount *
        (viewport * sdfProgramLayout.instanceCount + instance);
}

// Enclose a primitive's positive sublevel set, not merely its zero surface: primary accepts a band whose
// width depends on pixel footprint and the field's distance corrections. Unknown formulae decline the cache.
bool sdfPartShapeBox(uint type, float4 d0, float4 d1, float level, out float3 box) {
    box = 0.0;
    if (type == SDF_SHAPE_SUPERELLIPSOID) {
        float minRadius = min(d0.x, min(d0.y, d0.z));
        if (minRadius <= 0.0 || any(d1.yzw <= 0.0) || d0.w < 2.0) return false;
        // The Lp norm is at least each absolute coordinate. Its field scale is the minimum radius.
        box = (1.0 + level / minRadius) / d1.yzw;
    } else if (type == SDF_SHAPE_BOX || type == SDF_SHAPE_SCREEN_SLAB) {
        box = abs(d0.xyz) + level + abs(d0.w);
    } else if (type == SDF_SHAPE_CYLINDER) {
        box = float3(abs(d0.x), abs(d0.y), abs(d0.x)) + level + abs(d1.w);
    } else if (type == SDF_SHAPE_ELLIPSE || type == SDF_SHAPE_CONVEX_POLYGON) {
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

bool sdfPartSublevelBox(uint4 part, float depth, float footprint, out float3 lower, out float3 upper) {
    lower = 1e20;
    upper = -1e20;
    float scopeScale = asfloat(part.w) * sdfProgramLayout.stepScale;
    if (!(scopeScale > 0.0) || !isfinite(scopeScale)) return false;
    float level = max(SurfaceEpsilon, footprint * depth) / scopeScale;
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
        uint4 domain = 0u;
        float4 domain0 = 0.0, domain1 = 0.0;
        float correction = 1.0;
        if (code.y != 0u) {
            uint domainIndex = code.y - 1u;
            domain = sdfWords[1u + domainIndex];
            domain0 = asfloat(sdfWords[sdfProgramLayout.dataOffset + 2u * domainIndex]);
            domain1 = asfloat(sdfWords[sdfProgramLayout.dataOffset + 2u * domainIndex + 1u]);
            if (domain.x == SDF_OP_SCALE) correction = domain0.w;
            else if (domain.x == SDF_OP_AXIAL_PROFILE) correction = domain1.x;
            else if (domain.x != SDF_OP_SHEAR) return false;
            if (!(correction > 0.0) || !isfinite(correction)) return false;
        }
        float4 shape0 = asfloat(sdfWords[sdfProgramLayout.dataOffset + 2u * code.x]);
        float4 shape1 = asfloat(sdfWords[sdfProgramLayout.dataOffset + 2u * code.x + 1u]);
        float3 box;
        if (!sdfPartShapeBox(shape.y & SDF_SHAPE_TYPE_MASK, shape0, shape1, level / correction, box)) return false;
        if (code.y != 0u) {
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
        uint poseBinding = sdfWords[part.y + leaf].x;
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
        lower = min(lower, center - box);
        upper = max(upper, center + box);
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
            if (!sdfPartSublevelBox(part, depth, footprint, nextLower, nextUpper)) {
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
}

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
