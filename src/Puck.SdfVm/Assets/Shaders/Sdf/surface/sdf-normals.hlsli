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

// Per-pixel query tally for world.debug-view evals, including primary local-part marches and shading probes.
// Call sites here and in sdf-primary.hlsli count their queries; the interpreter does not. This per-thread
// scalar follows the material-seam channel's pattern and resets at renderView entry. Counting stays active
// for every view so selecting the evaluation heatmap does not change the work being measured.
static float sdfEvalCount = 0.0;

// The 4-tap TETRAHEDRON normal probe, MASKED (world path): estimates the field gradient from 4 samples at the corners
// of a tetrahedron (offset directions k.xyy/k.yyx/k.yxy/k.xxx = the alternating cube corners) instead of 6 axis-aligned
// samples. The taps are isotropic — Σ dᵢdᵢᵀ = 4·I and Σ dᵢ = 0 — so weighting each sample by its own direction
// reconstructs the SAME first-order gradient as the 6-tap central difference, from 4 evaluations instead of 6.
// Visually identical for lit shading (the O(ε) vs O(ε²) curvature error is sub-LSB at this ε), at 2/3 the cost of the
// kernel's hottest call. Every tap shares the pixel's tile instance mask — sound because a masked-out instance is
// exactly as absent from a nearby tap as it is from the hit itself (the beam prepass's tile cone covers the whole
// tile, taps included at this epsilon). The per-program stepScale is a common factor that cancels under
// normalize, so the Lipschitz clamp leaves normals untouched.
// gradientMagnitude (out): the secondary-ray gradient-scaling posture (src/Puck.World/Assets/pipelines/moth.hlsl's surfaceGradient) — the
// tetrahedron sum's own magnitude divided by 4e recovers the RAW field's local gradient magnitude at the hit
// (BEFORE this normalize), still carrying the taps' own mapDistanceMasked stepScale bake, so it is divided back out
// by sdfStepScale() to land in the SAME program-stepScale-independent units calculateNormalAnalytic reports (see
// GradientMagnitudeFloor above). The normal direction itself is unaffected — this is a second, additive return.
float3 calculateNormal(float3 p, uint instanceMaskBase, out float gradientMagnitude) {
    const float2 k = float2(1.0, -1.0);
    const float e = NormalProbeEpsilon;

    sdfEvalCount += 4.0; // four mapDistanceMasked taps below

    float3 sum =
        (k.xyy * mapDistanceMasked(p + (k.xyy * e), instanceMaskBase)) +
        (k.yyx * mapDistanceMasked(p + (k.yyx * e), instanceMaskBase)) +
        (k.yxy * mapDistanceMasked(p + (k.yxy * e), instanceMaskBase)) +
        (k.xxx * mapDistanceMasked(p + (k.xxx * e), instanceMaskBase));

    gradientMagnitude = ((length(sum) / (4.0 * e)) / sdfStepScale());

    return normalize(sum);
}
// The tetrahedron's four distances minus four times the center recover 2*e^2 times the field Laplacian.
// De-scale it to world units: concave creases read negative, convex ridges positive. The primary hit supplies
// the center unless Detail shapes can change the shading field; those programs query the current field again.
float3 calculateNormalCurvature(float3 p, uint instanceMaskBase, float primaryCenter, out float curvature, out float gradientMagnitude) {
    const float e = NormalProbeEpsilon;
    sdfEvalCount += 4.0;
    float3 sum = 0.0;
    float total = 0.0;
    // Keep one interpreter call site: unrolling duplicates the large VM body and slows the views kernel.
    [loop]
    for (uint probe = 0u; probe < 4u; probe++) {
        float3 direction = float3((probe == 0u || probe == 3u) ? 1.0 : -1.0,
            probe >= 2u ? 1.0 : -1.0, (probe & 1u) != 0u ? 1.0 : -1.0);
        float distance = mapDistanceMasked(p + (direction * e), instanceMaskBase);
        sum += direction * distance;
        total += distance;
    }
    float center = primaryCenter;
    if (!sdfProgramLayout.noDetailShapes) {
        center = mapDistanceMasked(p, instanceMaskBase);
        sdfEvalCount += 1.0;
    }
    float stepScale = sdfStepScale();
    curvature = ((total - (4.0 * center)) / ((2.0 * e * e) * stepScale));
    gradientMagnitude = ((length(sum) / (4.0 * e)) / stepScale);

    return normalize(sum);
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

    mapGradMasked(p, instanceMaskBase, gradient);

    gradientMagnitude = length(gradient);

    return sdfSafeNormalize(gradient);
}
#endif
