// The surface normal probes: finite-difference, curvature and analytic.
#ifndef SURFACE_SDF_NORMALS_HLSLI
#define SURFACE_SDF_NORMALS_HLSLI
// The gradient probe's finite-difference offset. Small enough that the tetrahedron's O(eps) curvature error is
// sub-LSB, large enough to stay clear of the field's own float noise.
static const float NormalProbeEpsilon = 0.0006;
// GRADIENT-SCALED PENUMBRA/AO (secondary-ray posture, src/Puck.World/Assets/pipelines/moth.hlsl's surfaceGradient/shadow/ambientOcclusion).
// mapCore/mapGradCore's per-program stepScale (sdfStepScale) is a single GLOBAL, WORST-CASE Lipschitz bound for the
// whole program/scope — it keeps the march SOUND but says nothing about how far a given shape's own formula departs
// from a unit SDF AT THE HIT (an ellipsoid gauge's sub-unit slope toward its long-axis tips, AxialProfile's y-varying shear, the study's
// own hand-authored `d*.7`-style scalar distance multiplies). The RAW gradient mapGradMasked returns (before its
// consumer normalizes) already carries that local departure — it is the gradient of the same shape-formula distance
// mapCore returns before ITS OWN final stepScale multiply (field/sdf-map.hlsli: "result.distance *= stepScale;" is NOT
// mirrored onto `gradient`). Its magnitude is therefore a SEPARATE, per-hit correction from stepScale, and the two
// compose multiplicatively into one effective de-scale factor (see shadingStepScale at the softShadowVisibility/
// calcAO call sites below) — never folded into stepScale itself, which must stay the program's own march-soundness
// bound. GradientMagnitudeFloor keeps a near-degenerate local gradient (a cusp, a blend seam) from blowing the
// estimate up; it mirrors the reference study's own clamp lower bound (src/Puck.World/Assets/pipelines/moth.hlsl, clamp(magnitude,.12,1.5)).
static const float GradientMagnitudeFloor = 0.12;

// The soften layer's wide-stencil probe offset (SdfMaterial.Soften): the same tetrahedron at a much larger epsilon, so
// fine surface detail (pores, panel seams, wear noise) washes out of the gradient it reads.
static const float SdfSoftenProbeEpsilon = 0.05;

// The tetrahedron's probe directions, the alternating cube corners k.xyy, k.yyx, k.yxy and k.xxx of k = (1, -1), in the
// order every tap sum adds them.
float3 sdfTetrahedronDirection(uint probe) {
    return float3(((probe == 0u) || (probe == 3u)) ? 1.0 : -1.0, (probe >= 2u) ? 1.0 : -1.0, ((probe & 1u) != 0u) ? 1.0 : -1.0);
}

// What one sdfProbeField call measured: the gradient taps' direction-weighted sum and plain total, the centre tap, and the
// soften taps' direction-weighted sum. A probe not asked for reads zero.
struct SdfFieldProbes {
    float3 gradientSum;
    float tapTotal;
    float center;
    float3 softenSum;
};

// The probe slots, in the order every probe runs: slots 0 to 3 are the four NormalProbeEpsilon tetrahedron taps, slot 4
// the centre tap at the probed point itself, and slots 5 to 8 the four SdfSoftenProbeEpsilon soften taps. A run asks for
// the slots from `first` up to `end`, skipping the centre tap unless it was asked for (sdfProbeSlotSkipped).
void sdfProbeSlots(bool gradientTaps, bool centerTap, bool softenTaps, out uint first, out uint end) {
    first = (gradientTaps ? 0u : (centerTap ? 4u : 5u));
    end = (softenTaps ? 9u : (centerTap ? 5u : 4u));
}
bool sdfProbeSlotSkipped(uint slot, bool centerTap) {
    return ((slot == 4u) && !centerTap);
}
// Where a probe slot samples the field around p.
float3 sdfProbeSlotPoint(float3 p, uint slot) {
    bool soften = (slot >= 5u);
    float3 direction = sdfTetrahedronDirection(soften ? (slot - 5u) : slot);

    return ((slot == 4u) ? p : (p + (direction * (soften ? SdfSoftenProbeEpsilon : NormalProbeEpsilon))));
}
// Adds one probe slot's distance to the probes it measures.
void sdfProbeSlotTake(inout SdfFieldProbes probes, uint slot, float distance) {
    bool soften = (slot >= 5u);
    float3 direction = sdfTetrahedronDirection(soften ? (slot - 5u) : slot);

    if (slot < 4u) {
        probes.gradientSum += (direction * distance);
        probes.tapTotal += distance;
    } else if (slot == 4u) {
        probes.center = distance;
    } else {
        probes.softenSum += (direction * distance);
    }
}

// Runs any of the surface's field probes through ONE interpreter call site, in slot order (sdfProbeSlots). Every map
// call DXC sees is a whole inlined interpreter, so a kernel that needs several probes asks for them in one call with its
// flags, never in a call per probe: the loop stays rolled, and each probe's samples and sums are the ones its own loop
// would take. The caller counts the evaluations it asked for. A kernel with other field reads runs the same slots
// through its own one field loop instead (sdfViewsFieldReads).
SdfFieldProbes sdfProbeField(float3 p, uint instanceMaskBase, bool gradientTaps, bool centerTap, bool softenTaps) {
    SdfFieldProbes probes = (SdfFieldProbes)0;
    uint first;
    uint end;

    sdfProbeSlots(gradientTaps, centerTap, softenTaps, first, end);

    [loop]
    for (uint slot = first; (slot < end); slot++) {
        if (sdfProbeSlotSkipped(slot, centerTap)) {
            continue;
        }

        sdfProbeSlotTake(probes, slot, mapDistanceMasked(sdfProbeSlotPoint(p, slot), instanceMaskBase));
    }

    return probes;
}
// The curvature the tap probe measured around a center distance. The tetrahedron's four distances minus four times the
// center recover 2*e^2 times the field Laplacian; de-scaled to world units, concave creases read negative and convex
// ridges positive.
float sdfProbeCurvature(SdfFieldProbes probes, float center) {
    const float e = NormalProbeEpsilon;

    return ((probes.tapTotal - (4.0 * center)) / ((2.0 * e * e) * sdfStepScale()));
}
// The 4-tap TETRAHEDRON normal probe, MASKED (world path): estimates the field gradient from 4 samples at the corners
// of a tetrahedron (sdfTetrahedronDirection) instead of 6 axis-aligned samples. The taps are isotropic — Σ dᵢdᵢᵀ = 4·I
// and Σ dᵢ = 0 — so weighting each sample by its own direction reconstructs the SAME first-order gradient as the 6-tap
// central difference, from 4 evaluations instead of 6. Visually identical for lit shading (the O(ε) vs O(ε²) curvature
// error is sub-LSB at this ε), at 2/3 the cost of the kernel's hottest call. Every tap shares the pixel's tile instance
// mask — sound because a masked-out instance is exactly as absent from a nearby tap as it is from the hit itself (the
// beam prepass's tile cone covers the whole tile, taps included at this epsilon). The per-program stepScale is a common
// factor that cancels under normalize, so the Lipschitz clamp leaves normals untouched.
// curvature (out): with withCurvature, sdfProbeCurvature around the center; zero without. The visibility record supplies
// the center unless Detail shapes can change the shading field; those programs query the current field again, as the
// same probe's centre tap.
// gradientMagnitude (out): the secondary-ray gradient-scaling posture (src/Puck.World/Assets/pipelines/moth.hlsl's surfaceGradient) — the
// tetrahedron sum's own magnitude divided by 4e recovers the RAW field's local gradient magnitude at the hit
// (BEFORE this normalize), still carrying the taps' own mapDistanceMasked stepScale bake, so it is divided back out
// by sdfStepScale() to land in the SAME program-stepScale-independent units calculateNormalAnalytic reports (see
// GradientMagnitudeFloor above). The normal direction itself is unaffected — this is a second, additive return.
float3 calculateTapNormal(float3 p, uint instanceMaskBase, bool withCurvature, float primaryCenter, out float curvature, out float gradientMagnitude) {
    const float e = NormalProbeEpsilon;
    bool centerTap = (withCurvature && !sdfProgramLayout.noDetailShapes);

    sdfEvalCount += 4.0;
    sdfWorkSteps += 4u;

    SdfFieldProbes probes = sdfProbeField(p, instanceMaskBase, true, centerTap, false);
    float center = primaryCenter;

    if (centerTap) {
        center = probes.center;
        sdfEvalCount += 1.0;
        sdfWorkSteps += 1u;
    }

    curvature = (withCurvature ? sdfProbeCurvature(probes, center) : 0.0);
    gradientMagnitude = ((length(probes.gradientSum) / (4.0 * e)) / sdfStepScale());

    return normalize(probes.gradientSum);
}
// The ANALYTIC surface normal (forward-mode gradient dual): ONE dual field eval at the hit — replacing the four taps —
// carries the exact world-space field gradient through the transform chain (field/sdf-map-grad.hlsli's mapGradMasked). Immune to
// the finite-difference catastrophic cancellation the taps suffer near a warp/fold/displace, and more cross-backend-
// stable near these discontinuities. mapGradMasked returns the UN-normalized gradient; the stepScale the scalar
// distance still carries is a uniform positive factor that cancels under this normalize, so the dual never applies it.
// Same tile instance mask as the primary march, so the analytic normal sees the identical masked field the hit did.
// gradientMagnitude (out): mapGradMasked's `gradient` is ALREADY the RAW, program-stepScale-EXCLUDED field gradient
// (field/sdf-map-grad.hlsli's mapGradCore multiplies only `result.distance` by stepScale, never `gradient` — see the
// GradientMagnitudeFloor remarks above) — its length is this function's local gradient magnitude for free, no extra
// field evaluation.
float3 calculateNormalAnalytic(float3 p, uint instanceMaskBase, out float gradientMagnitude) {
    float3 gradient;

    sdfEvalCount += 1.0; // one dual field eval replaces the four taps
    sdfWorkSteps += 1u;

    mapGradMasked(p, instanceMaskBase, gradient);

    gradientMagnitude = length(gradient);

    return sdfSafeNormalize(gradient);
}
#endif
