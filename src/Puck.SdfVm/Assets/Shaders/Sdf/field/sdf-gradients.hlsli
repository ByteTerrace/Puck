// The analytic gradients and the dual blends the forward-mode normal composes.
#ifndef FIELD_SDF_GRADIENTS_HLSLI
#define FIELD_SDF_GRADIENTS_HLSLI
float3 sdfSafeNormalize(float3 v) {
    return (v * rsqrt(max(dot(v, v), 1.0e-24)));
}

// Applies a 3x3 point-Jacobian A (given as its three ROWS ax/ay/az) to each carried Jacobian column vector. Each new
// vector is a full float3 built from dot products of the OLD vector, so the in/out aliasing is safe.
void sdfApplyJacobian(float3 ax, float3 ay, float3 az, inout float3 jx, inout float3 jy, inout float3 jz) {
    jx = float3(dot(ax, jx), dot(ay, jx), dot(az, jx));
    jy = float3(dot(ax, jy), dot(ay, jy), dot(az, jy));
    jz = float3(dot(ax, jz), dot(ay, jz), dot(az, jz));
}

// --- analytic LEAF gradients (the cheap majority; exact for a metric SDF) ---
// The box: outside, the gradient is the outward direction of the exterior offset, signed per octant; inside, it points
// along the nearest face's axis. cornerRadius rounds the surface but not the gradient direction.
float3 sdfBoxGradient(float3 p, float3 halfExtents, float cornerRadius) {
    float3 s = float3((p.x < 0.0) ? -1.0 : 1.0, (p.y < 0.0) ? -1.0 : 1.0, (p.z < 0.0) ? -1.0 : 1.0);
    float3 q = (abs(p) - (halfExtents - cornerRadius));
    float m = max(q.x, max(q.y, q.z));

    if (m > 0.0) {
        return (s * sdfSafeNormalize(max(q, 0.0)));
    }

    float3 axis = float3((q.x >= m) ? 1.0 : 0.0, (q.y >= m) ? 1.0 : 0.0, (q.z >= m) ? 1.0 : 0.0);

    return (s * sdfSafeNormalize(axis));
}
float3 sdfTorusGradient(float3 p, float major, float minor) {
    float lxz = length(p.xz);
    float2 q = float2((lxz - major), p.y);
    float lq = max(length(q), 1.0e-12);
    float2 radial = ((lxz > 1.0e-12) ? (p.xz / lxz) : float2(0.0, 0.0));

    return float3(((q.x / lq) * radial.x), (q.y / lq), ((q.x / lq) * radial.y));
}
float3 sdfCapsuleGradient(float3 p, float3 endpoint, float inverseLengthSquared) {
    float h = clamp((dot(p, endpoint) * inverseLengthSquared), 0.0, 1.0);

    return sdfSafeNormalize(p - (h * endpoint));
}
float3 sdfCylinderGradient(float3 p, float radius, float halfHeight) {
    float lxz = length(p.xz);
    float2 radial = ((lxz > 1.0e-12) ? (p.xz / lxz) : float2(0.0, 0.0));
    float ySign = ((p.y < 0.0) ? -1.0 : 1.0);
    float2 d = (float2(lxz, abs(p.y)) - float2(radius, halfHeight));

    if (max(d.x, d.y) <= 0.0) {
        return ((d.x > d.y) ? float3(radial.x, 0.0, radial.y) : float3(0.0, ySign, 0.0));
    }

    float2 e = max(d, 0.0);
    float le = max(length(e), 1.0e-12);
    float2 n = (e / le);

    return float3((n.x * radial.x), (n.y * ySign), (n.x * radial.y));
}
// Shape-LOCAL 4-tap tetrahedron FD for the exotic primitives (vesica/roundcone/the 2D-lifted family, and the
// atlas-sampled Glyph): a tight difference of just that one primitive's SDF in folded space, no transform chain — so it
// fixes the op-CHAIN propagation (the real win) with a cheap, cancellation-light leaf. The Glyph's taps re-sample the
// atlas (band-culled), which is why the honest leaf is a shape-local FD, not an analytic gradient. Same isotropic
// tetrahedron the world normal uses.
float3 sdfShapeGradientFd(uint shapeType, float3 p, float4 data0, float4 data1) {
    const float2 k = float2(1.0, -1.0);
    const float e = SDF_SHAPE_GRAD_EPSILON;

    return sdfSafeNormalize(
        (k.xyy * evaluateShape(shapeType, (p + (k.xyy * e)), data0, data1)) +
        (k.yyx * evaluateShape(shapeType, (p + (k.yyx * e)), data0, data1)) +
        (k.yxy * evaluateShape(shapeType, (p + (k.yxy * e)), data0, data1)) +
        (k.xxx * evaluateShape(shapeType, (p + (k.xxx * e)), data0, data1)));
}
// The normalized gradient of the ellipsoid gauge (|p/r| - 1) * min(r): p / r^2, normalized. Zero at the center, which
// has no unique normal (sdfSafeNormalize of the zero vector is zero), matching sdfSuperellipsoidGradient. KEEP IN SYNC
// with sdfEllipsoidGauge.
float3 sdfEllipsoidGaugeGradient(float3 p, float3 inverseRadii) {
    return sdfSafeNormalize(p * (inverseRadii * inverseRadii));
}
#ifndef SDF_STRIP_HEAVY
// The normalized gradient of min(r) * (sum(abs(p/r)^e)^(1/e) - 1). Its common positive factor cancels
// on normalization, leaving sign(p_i) * abs(p_i/r_i)^(e-1) / r_i. Factor by max(abs(p/r)) before pow,
// as in sdfSuperellipsoid, to avoid overflowing for distant samples. The center has no unique normal.
// Return a unit direction like the previous shape-local FD path; transform/CSG gradient transport is unchanged.
float3 sdfSuperellipsoidGradient(float3 p, float3 inverseRadii, float exponent) {
    if (exponent == 2.0) {
        return sdfEllipsoidGaugeGradient(p, inverseRadii);
    }

    float3 q = (abs(p) * inverseRadii);
    float m = max(q.x, max(q.y, q.z));
    if (m <= 0.0) {
        return float3(0.0, 0.0, 0.0);
    }
    return sdfSafeNormalize(sign(p) * pow(q / m, (exponent - 1.0)) * inverseRadii);
}
#endif
// The gradient companion to evaluateShape (same dispatch): analytic for the cheap majority, shape-local FD for the rest.
float3 evaluateShapeGradient(uint shapeType, float3 p, float4 data0, float4 data1) {
    switch (shapeType) {
        case SDF_SHAPE_SPHERE:      return sdfSafeNormalize(p);
        // The plane's gradient is its (host-normalized) normal, exactly.
        case SDF_SHAPE_PLANE:       return data0.xyz;
        case SDF_SHAPE_BOX:
        case SDF_SHAPE_SCREEN_SLAB: return sdfBoxGradient(p, data0.xyz, data0.w);
#ifndef SDF_STRIP_ALL_EXOTIC
        case SDF_SHAPE_TORUS:       return sdfTorusGradient(p, data0.x, data0.y);
#endif
        case SDF_SHAPE_CAPSULE:     return sdfCapsuleGradient(p, data0.xyz, data1.y);
        case SDF_SHAPE_CYLINDER:    return sdfCylinderGradient(p, data0.x, data0.y);
#ifndef SDF_STRIP_ALL_EXOTIC
        // Same tiering as evaluateShape's case: the fold tier only ever sees e == 2.
#ifdef SDF_STRIP_HEAVY
        case SDF_SHAPE_SUPERELLIPSOID: return sdfEllipsoidGaugeGradient(p, data1.yzw);
#else
        case SDF_SHAPE_SUPERELLIPSOID: return sdfSuperellipsoidGradient(p, data1.yzw, data0.w);
#endif
#endif
        // The exotic tail — the 2D-lift family, Glyph, and SDF_SHAPE_SAMPLED_REGION — falls to the shape-local 4-tap FD
        // (the analytic-dual doctrine already pays FD for Star/Ellipse here). For a brick that is 4 extra pool samples,
        // hit-only; an analytic trilinear gradient is a recorded follow-up. FD holds in both variants because the
        // SDF_SHAPE_SAMPLED_REGION arm of evaluateShape is compiled in both (not stripped under SDF_CORE_OPS).
        default:                    return sdfShapeGradientFd(shapeType, p, data0, data1);
    }
}
// The gradient-carrying twin of blendShape: reproduces its distance branch-for-branch AND propagates the world-space
// surface gradient. HARD blends SELECT the winning branch's gradient (negated where the distance formula negates the
// candidate — the subtraction sign bug lives here). SMOOTH blends LERP the two gradients by the SAME h weight the
// distance lerp uses; the exact smin gradient carries an extra tangential term, but the normal normalizes and the SOTA
// survey accepts the standard lerp approximation, so it is used here. CHAMFER blends select the winning one of their
// three terms (the bevel term's gradient is the summed/differenced unit gradients times sqrt(1/2)).
void blendShapeDual(float current, float3 currentGrad, float candidate, float3 candidateGrad, uint blendOp, float smoothRadius, out float outDist, out float3 outGrad) {
    float smoothK = max(smoothRadius, SDF_SMOOTH_RADIUS_MIN);
    float chamfer = max(smoothRadius, 0.0);

    outDist = min(current, candidate);                                  // SDF_BLEND_UNION (the default)
    outGrad = ((candidate < current) ? candidateGrad : currentGrad);

    switch (blendOp) {
        case SDF_BLEND_SMOOTH_UNION: {
            float h = clamp((0.5 + ((0.5 * (candidate - current)) / smoothK)), 0.0, 1.0);
            outDist = blendSmoothUnion(current, candidate, smoothK);
            outGrad = lerp(currentGrad, candidateGrad, (1.0 - h));
            break;
        }
        case SDF_BLEND_SUBTRACTION: {
            outDist = max(current, -candidate);
            outGrad = (((-candidate) > current) ? (-candidateGrad) : currentGrad);
            break;
        }
        case SDF_BLEND_INTERSECTION: {
            outDist = max(current, candidate);
            outGrad = ((candidate > current) ? candidateGrad : currentGrad);
            break;
        }
        case SDF_BLEND_XOR: {
            float mn = min(current, candidate);
            float mx = max(current, candidate);
            outDist = max(mn, -mx);

            if ((-mx) > mn) {
                outGrad = ((current > candidate) ? (-currentGrad) : (-candidateGrad));
            }
            else {
                outGrad = ((current < candidate) ? currentGrad : candidateGrad);
            }

            break;
        }
        case SDF_BLEND_SMOOTH_INTERSECTION: {
            float h = clamp((0.5 + ((0.5 * (current - candidate)) / smoothK)), 0.0, 1.0);
            outDist = -blendSmoothUnion(-current, -candidate, smoothK);
            outGrad = lerp(currentGrad, candidateGrad, (1.0 - h));
            break;
        }
        case SDF_BLEND_SMOOTH_SUBTRACTION: {
            float h = clamp((0.5 + ((0.5 * ((-current) - candidate)) / smoothK)), 0.0, 1.0);
            outDist = -blendSmoothUnion(candidate, -current, smoothK);
            outGrad = lerp((-candidateGrad), currentGrad, (1.0 - h));
            break;
        }
        case SDF_BLEND_GROOVE_UNION:
        case SDF_BLEND_PIPE_UNION:
        case SDF_BLEND_GROOVE_SUBTRACTION:
        case SDF_BLEND_PIPE_SUBTRACTION: {
            bool isSubtraction = (blendOp == SDF_BLEND_GROOVE_SUBTRACTION || blendOp == SDF_BLEND_PIPE_SUBTRACTION);
            bool groove = (blendOp == SDF_BLEND_GROOVE_UNION || blendOp == SDF_BLEND_GROOVE_SUBTRACTION);
            float baseDist = isSubtraction ? max(current, -candidate) : min(current, candidate);
            float3 baseGrad = isSubtraction
                ? (((-candidate) > current) ? (-candidateGrad) : currentGrad)
                : ((candidate < current) ? candidateGrad : currentGrad);
            outDist = baseDist;
            outGrad = baseGrad;

            float tubeLength = length(float2(current, candidate));
            float tube = tubeLength - chamfer;
            float seam = groove ? -tube : tube;
            if (groove ? seam > outDist : seam < outDist) {
                outDist = seam;
                // At a=b=0 the tube norm has no unique derivative; its symmetric derivative is zero.
                outGrad = tubeLength > 1.0e-12 ? (current * currentGrad + candidate * candidateGrad) / tubeLength : 0.0;
                if (groove) outGrad = -outGrad;
            }
            break;
        }
        case SDF_BLEND_CHAMFER_UNION: {
            float bevel = ((current + candidate - chamfer) * SDF_SQRT_HALF);
            outDist = min(min(current, candidate), bevel);
            outGrad = currentGrad;
            if (candidate < current) { outGrad = candidateGrad; }
            if (bevel <= min(current, candidate)) { outGrad = ((currentGrad + candidateGrad) * SDF_SQRT_HALF); }
            break;
        }
        case SDF_BLEND_CHAMFER_INTERSECTION: {
            float bevel = ((current + candidate + chamfer) * SDF_SQRT_HALF);
            outDist = max(max(current, candidate), bevel);
            outGrad = currentGrad;
            if (candidate > current) { outGrad = candidateGrad; }
            if (bevel >= max(current, candidate)) { outGrad = ((currentGrad + candidateGrad) * SDF_SQRT_HALF); }
            break;
        }
        case SDF_BLEND_CHAMFER_SUBTRACTION: {
            float bevel = ((current - candidate + chamfer) * SDF_SQRT_HALF);
            outDist = max(max(current, -candidate), bevel);
            outGrad = currentGrad;
            if ((-candidate) > current) { outGrad = -candidateGrad; }
            if (bevel >= max(current, -candidate)) { outGrad = ((currentGrad - candidateGrad) * SDF_SQRT_HALF); }
            break;
        }
        case SDF_BLEND_MORPH:
        case SDF_BLEND_STAIRS_UNION: {
            outDist = min(current, candidate);
            outGrad = (candidate < current) ? candidateGrad : currentGrad;
            break;
        }
        case SDF_BLEND_STAIRS_SUBTRACTION: {
            outDist = max(current, -candidate);
            outGrad = ((-candidate) > current) ? (-candidateGrad) : currentGrad;
            break;
        }
    }
}
// The dual twin of sdfComposeCandidate: the interpreted dual walk and the host-compiled rigid-leaf dual fast path meet
// here so the two cannot drift into subtly different blend VMs (the same reason the scalar paths share
// sdfComposeCandidate). The strict material-winner compare runs BEFORE the distance/gradient blend — order-dependent
// blend semantics preserved. HIT-ONLY: resolves the winning material and the world gradient, never the smooth-seam
// material blend channel (the primary stage captures that from the scalar accept-sample march), exactly as it skips
// sdfMapStepBound for being hit-only.
void sdfComposeDualCandidate(inout SdfHit result, inout float3 resultGradient, float candidate, float3 candidateGrad, uint blend, int material, float4 lanes, int frameSlot, float smooth) {
    bool candidateWins;

    switch (blend) {
        case SDF_BLEND_INTERSECTION:
        case SDF_BLEND_SMOOTH_INTERSECTION:
        case SDF_BLEND_CHAMFER_INTERSECTION: { candidateWins = (candidate > result.distance); break; }
        case SDF_BLEND_SUBTRACTION:
        case SDF_BLEND_SMOOTH_SUBTRACTION:
        case SDF_BLEND_CHAMFER_SUBTRACTION:
        case SDF_BLEND_GROOVE_SUBTRACTION:
        case SDF_BLEND_PIPE_SUBTRACTION:  { candidateWins = (-candidate > result.distance); break; }
        default:                             { candidateWins = (candidate < result.distance); break; }
    }

    if (candidateWins) {
        result.material = material;
        result.lanes = lanes;
        result.frameSlot = frameSlot;
    }

    float blendedDistance;
    float3 blendedGradient;
    blendShapeDual(result.distance, resultGradient, candidate, candidateGrad, blend, smooth, blendedDistance, blendedGradient);
    result.distance = blendedDistance;
    resultGradient = blendedGradient;
}
#endif
