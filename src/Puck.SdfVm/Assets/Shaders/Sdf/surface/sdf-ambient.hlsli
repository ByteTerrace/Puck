// The field de-scale the shading walks share, and ambient occlusion.
#ifndef SURFACE_SDF_AMBIENT_HLSLI
#define SURFACE_SDF_AMBIENT_HLSLI
// De-scale a Lipschitz-CLAMPED field sample back to WORLD units — the ONE primitive genuinely shared by the three
// shading-epilogue field walks (softShadowVisibility, calcAO, coverage-AA). mapMasked/map return the field pre-multiplied by the
// per-program stepScale (the <= 1-Lipschitz clamp); a consumer that COMPARES a sample against a world-space
// quantity must divide that clamp back out FIRST, or its result tracks the program's stepScale bake instead of geometry
// — the ~30%-darkening chamfer bug (stepScale = 1/sqrt(2)) a prior fix already closed. The three consumers each divide
// it back for a DELIBERATELY DIFFERENT world-space comparison — this is the divide-back FOOT-GUN the docs warn re-fixers
// about, so factor only the divide, never the surrounding intent:
//   - softShadowVisibility — the RAW clamped sample drives the step advance (an under-step is conservative) and the
//                   occlusion test, which scales the THRESHOLD up instead; the penumbra ESTIMATE divides a clearance
//                   by the world-unit distance travelled, so that clearance is de-scaled.
//   - calcAO      — the rung distance d in the (h - d) open-space deficit (a world-space rung minus a field sample).
// Coverage AA deliberately does NOT de-scale its residual/threshold ratio: both operands use the clamped units
// of the footprint termination test. Background visibility comes from neighboring primary records, not a field probe.
// stepScale == 1.0 EXACTLY for an isometric, warp-free program and x / 1.0f == x to the bit, so those scenes stay
// byte-identical whether the divide inlines here or is spelled at the call site.
// GRADIENT-SCALED CALLERS: softShadowVisibility/calcAO receive a `stepScale` argument the light and
// ambient stages pre-compose as `stepScale * max(gradientMagnitude, GradientMagnitudeFloor)` — the program's own march
// clamp times the hit's LOCAL field gradient magnitude (see GradientMagnitudeFloor's remarks). This function stays
// unaware of the composition: it is still one division, so a warp-free, unit-gradient hit (both factors == 1.0)
// keeps every existing byte-identical guarantee.
float sdfDeScaleField(float clampedSample, float stepScale) {
    return (clampedSample / stepScale);
}

#include "sdf-occlusion.hlsli"

#endif
