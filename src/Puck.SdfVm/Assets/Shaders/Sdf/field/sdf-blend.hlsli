// The blends and the material winner that compose each shape's candidate into the running field.
#ifndef FIELD_SDF_BLEND_HLSLI
#define FIELD_SDF_BLEND_HLSLI
// The polynomial smooth minimum every smooth blend derives from (smoothIntersection/smoothSubtraction are its
// negations, so all three share one seam). BOTH saturated endpoints return their input TO THE BIT:
//
//  * FAR (b at least k beyond a): h clamps to exactly 1 and the endpoint returns a, including its zero sign. This lets a
//    masked-out smooth-blended instance (dropped past its cull bound) return the accumulator identically to evaluating
//    it, so a smooth instance can carry a FINITE, k-inflated bound instead of an unmaskable one (SdfProgram.PackInstances).
//    The usual lerp(b, a, h) form leaves candidate + (current - candidate) here, ~1 LSB off.
//  * NEAR (a at least k beyond b): h clamps to exactly 0, and the endpoint returns b. Without that select the
//    expression is a + (b - a), which is NOT b when |a| >> |b| — and `a` is SDF_FAR_DISTANCE (1e9) for the first shape
//    evaluated in a mapCore call. blendSmoothUnion(1e9, 5, 0.2) then returns 0 rather than 5, collapsing the whole field
//    to a surface at the sample. Reachable whenever a segment's first shape carries SmoothUnion and every earlier
//    segment was bound-skipped. The select costs one cndsel and makes "smooth-union with an empty accumulator" mean
//    what it must: the shape itself.
float blendSmoothUnion(float a, float b, float k) {
    float h = clamp((0.5 + ((0.5 * (b - a)) / k)), 0.0, 1.0);
    if (h <= 0.0) { return b; }
    if (h >= 1.0) { return a; }
    float blended = lerp(a, b, (1.0 - h));

    return (blended - ((k * h) * (1.0 - h)));
}
float blendShape(float current, float candidate, uint blendOp, float smoothRadius) {
    float result = min(current, candidate);                     // SDF_BLEND_UNION (the default)
    float smoothK = max(smoothRadius, SDF_SMOOTH_RADIUS_MIN);   // the SMOOTH arms' shared radius floor
    // The CHAMFER arms clamp against 0.0, not SDF_SMOOTH_RADIUS_MIN: a zero bevel must be exactly a hard seam, and
    // folding the smooth floor in would shift the bevel plane. Do not merge the two clamps.
    float chamfer = max(smoothRadius, 0.0);

    switch (blendOp) {
        case SDF_BLEND_SMOOTH_UNION:        result = blendSmoothUnion(current, candidate, smoothK); break;
        case SDF_BLEND_SUBTRACTION:         result = max(current, -candidate); break;
        case SDF_BLEND_INTERSECTION:        result = max(current, candidate); break;
        case SDF_BLEND_XOR:                 result = max(min(current, candidate), -max(current, candidate)); break;
        case SDF_BLEND_SMOOTH_INTERSECTION: result = -blendSmoothUnion(-current, -candidate, smoothK); break;
        case SDF_BLEND_SMOOTH_SUBTRACTION:  result = -blendSmoothUnion(candidate, -current, smoothK); break;
        // Chamfered (45-degree bevel) seams (hg_sdf fOp*Chamfer): the bevel plane is (a +- r + b) * sqrt(1/2). Union
        // bevels the near corner, intersection/subtraction the far one; SUBTRACTION is the intersection of `current`
        // with -candidate. The bevel plane's gradient reaches sqrt(2) at a FLAT/near-parallel seam (1 at a perpendicular
        // one, 0 at an acute one), hence the per-composition chamfer step clamp in SdfProgram.AnalyzeLipschitz — see the
        // SDF_BLEND_CHAMFER_* banner for the max(La, Lb, (La + Lb)/sqrt(2)) recurrence it folds.
        case SDF_BLEND_GROOVE_UNION: result = max(min(current, candidate), chamfer - length(float2(current, candidate))); break;
        case SDF_BLEND_PIPE_UNION: result = min(min(current, candidate), length(float2(current, candidate)) - chamfer); break;
        case SDF_BLEND_GROOVE_SUBTRACTION: result = max(max(current, -candidate), chamfer - length(float2(current, candidate))); break;
        case SDF_BLEND_PIPE_SUBTRACTION: result = min(max(current, -candidate), length(float2(current, candidate)) - chamfer); break;
        case SDF_BLEND_CHAMFER_UNION:        result = min(min(current, candidate), ((current + candidate - chamfer) * SDF_SQRT_HALF)); break;
        case SDF_BLEND_CHAMFER_INTERSECTION: result = max(max(current, candidate), ((current + candidate + chamfer) * SDF_SQRT_HALF)); break;
        case SDF_BLEND_CHAMFER_SUBTRACTION:  result = max(max(current, -candidate), ((current - candidate + chamfer) * SDF_SQRT_HALF)); break;
        case SDF_BLEND_MORPH:
        case SDF_BLEND_STAIRS_UNION:        result = min(current, candidate); break;
        case SDF_BLEND_STAIRS_SUBTRACTION:  result = max(current, -candidate); break;
    }

    return result;
}

// The scalar interpreter and the host-compiled rigid-leaf path meet here. Keeping the material winner, smooth seam
// channel, and distance compose in one helper prevents the fast path from becoming a subtly different VM. DXC sees
// trackMaterial as a literal at every call and erases this whole material half for the bake's gradient taps.
void sdfComposeCandidate(inout SdfHit result, float candidate, uint blend, int material, float4 lanes, int instanceIndex, int frameSlot, float smooth, bool trackMaterial) {
    if (trackMaterial) {
        bool candidateWins;

        switch (blend) {
            case SDF_BLEND_INTERSECTION:
            case SDF_BLEND_SMOOTH_INTERSECTION:
            case SDF_BLEND_CHAMFER_INTERSECTION: { candidateWins = (candidate > result.distance); break; }
            case SDF_BLEND_SUBTRACTION:
            case SDF_BLEND_SMOOTH_SUBTRACTION:
            case SDF_BLEND_CHAMFER_SUBTRACTION:
            case SDF_BLEND_GROOVE_SUBTRACTION:
            case SDF_BLEND_PIPE_SUBTRACTION:     { candidateWins = (-candidate > result.distance); break; }
            default:                             { candidateWins = (candidate < result.distance); break; }
        }

        int incumbentMaterial = result.material;

        switch (blend) {
            case SDF_BLEND_SMOOTH_UNION:
            case SDF_BLEND_SMOOTH_INTERSECTION:
            case SDF_BLEND_SMOOTH_SUBTRACTION: {
                float smoothK = max(smooth, SDF_SMOOTH_RADIUS_MIN);
                float signedGap = ((blend == SDF_BLEND_SMOOTH_UNION) ? (candidate - result.distance)
                                : ((blend == SDF_BLEND_SMOOTH_INTERSECTION) ? (result.distance - candidate)
                                : ((-result.distance) - candidate)));
                float h = clamp((0.5 + ((0.5 * signedGap) / smoothK)), 0.0, 1.0);
                int loserMaterial = (candidateWins ? incumbentMaterial : material);
                bool tableSeam = ((incumbentMaterial < SDF_SCREEN_MATERIAL) && (material < SDF_SCREEN_MATERIAL));

                sdfMaterialBlendWeight = (tableSeam ? min(h, (1.0 - h)) : 0.0);
                sdfMaterialBlendOther = loserMaterial;
                break;
            }
            default: {
                if (candidateWins) {
                    sdfMaterialBlendWeight = 0.0;
                }

                break;
            }
        }

        if (candidateWins) {
            result.material = material;
            result.lanes = lanes;
            result.instanceIndex = instanceIndex;
            result.frameSlot = frameSlot;
        }
    }

    result.distance = blendShape(result.distance, candidate, blend, smooth);
}

#endif
