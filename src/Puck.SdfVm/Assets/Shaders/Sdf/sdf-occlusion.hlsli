#ifndef PUCK_SDF_OCCLUSION_HLSLI
#define PUCK_SDF_OCCLUSION_HLSLI

// The soft-shadow visibility toward lightDirection from a surface point: 1 fully lit, 0 fully occluded. The running
// minimum of k · c / t along the ray — the clearance the field reports at each sample over the distance travelled —
// marched by that clearance under a distance-proportional step ceiling. The estimate is self-sampling: the step never
// exceeds the clearance, so samples crowd toward any close approach and the sample nearest it reads a clearance
// within one step of the true minimum. The closest-approach fold (the chord between consecutive clearance spheres)
// is deliberately NOT used: it reads the previous sample too, so whether a pair straddles the close approach flips
// across neighbouring rays whenever the occluder is thinner than the step, banding a thin rim's penumbra into
// alternating lit and dark stripes; the fold also collapses to zero on a ray leaving its own surface along the
// normal, where the clearance doubles every step. The advance honors the published fold
// boundary (sdfMapStepBound) and the occlusion test reads the raw clearance in clamped units against the primary
// march's own accept threshold; the estimate divides a clearance by the world-unit distance travelled, so its
// clearance is de-scaled first (a clamped clearance would narrow a program's shadows with its stepScale bake).
float softShadowVisibility(float3 surfacePoint, float3 surfaceNormal, float3 lightDirection, uint instanceMaskBase, float stepScale, float reach) {
    bool fastMarch = worldUseFastSoftShadowMarch();
    int stepBudget = (fastMarch ? FastShadowSteps : ShadowSteps);
    float sharpness = (1.0 / worldShadowPenumbraSlope());
    float3 origin = (surfacePoint + (surfaceNormal * ShadowBias));
    float traveled = ShadowBias;
    float visibility = 1.0;

    reach = (fastMarch ? min(reach, FastShadowMaxDistance) : reach);

    [loop]
    for (int step = 0; (step < stepBudget); step++) {
        float clearance = mapDistanceMasked(origin + (lightDirection * traveled), instanceMaskBase);

        sdfEvalCount += 1.0;

        if (clearance < (SurfaceEpsilon * stepScale)) {
            return 0.0;
        }

        // Samples closer to the origin than ShadowEstimateStart read the origin surface itself (a ray skimming its
        // own curved surface at grazing incidence) and are skipped, never clamped.
        if (traveled >= ShadowEstimateStart) {
            visibility = min(visibility, ((sharpness * sdfDeScaleField(clearance, stepScale)) / traveled));
        }

        if (visibility < 0.005) {
            return 0.0;
        }

        float radius = min(clearance, sdfMapStepBound);
        float ceiling = (fastMarch
            ? max(FastShadowStepMax, (traveled * FastShadowStepFarSlope))
            : max(ShadowStepNear, (traveled * ShadowStepFarSlope)));

        traveled += clamp(radius, ShadowStepMin, ceiling);

        if (traveled > reach) {
            break;
        }
    }

    // A smoothstep so the penumbra's core and edge read as one soft band rather than a linear wedge.
    visibility = saturate(visibility);

    return ((visibility * visibility) * (3.0 - (2.0 * visibility)));
}
// Normal-ladder ambient occlusion (calcAO): from the hit, step a short ladder of fixed rungs OUTWARD along
// the surface normal; at each rung compare the distance expected to travel (h) against what the field actually reports
// (d) — where nearby geometry crowds the normal the field under-reports and the deficit (h - d) accumulates as
// occlusion, with an outer-rung falloff and a gain/clamp. THREE mapMasked() calls (was five): the rungs are
// re-spaced to span the SAME 0.01..0.13 reach at double pitch, the per-rung falloff squared (0.95^2 = 0.9025) to hold
// the same spatial decay, and the gain re-tuned 3.0 -> 5.07 so the fully-occluded floor matches the 5-tap value to
// ~0.01 (verified analytically over constant-factor and constant-gap occluder models) — a same-look AO at 60% of the
// taps. The rung loop is a [loop], NOT [unroll] (2026-09-03): every mapDistanceMasked call site inlines a full copy of
// the tape interpreter, so three unrolled rungs were three interpreter copies in the hottest kernel — measured on the
// RTX 2060 shipped world as a 60 ms AO term for three evaluations per lit pixel, ten times the primary march's cost per
// evaluation; one rolled call site is the fix, not fewer taps. Paid
// ONLY on lit hits. The exact path uses independently conservative sublevel candidates or the complete field;
// a camera cone cannot exclude an instance from a displaced normal probe. Purely local — no hemisphere or history — but reads as contact
// shadowing in creases and under overhangs.
//
// Applied to the AMBIENT/sky fill ONLY, never the sun: soft shadows govern direct light, and multiplying occlusion into
// direct light double-darkens it (occlusion and shadow would both attenuate the same light twice). The (h - d)
// subtract mixes a WORLD-space rung h with a mapMasked distance pre-scaled by the Lipschitz clamp, so d is divided
// back to world units FIRST (d / stepScale) — the same divide-back the shadow escape exit applies; without it occlusion strength
// tracks each program's stepScale bake, not geometry.
// `stepScale` combines the program clamp with the hit's geometric gradient magnitude. This is a local distance
// correction, not an exact Euclidean distance guarantee away from the hit.
static const float AmbientOcclusionReach = 0.13;
float calcAO(float3 surfacePoint, float3 surfaceNormal, uint instanceMaskBase, float stepScale) {
    float occlusion = 0.0;
    float scale = 1.0;

    [loop]
    for (int i = 0; (i < 3); i++) {
        float h = (0.01 + (((AmbientOcclusionReach - 0.01) * float(i)) / 2.0));
#ifdef SDF_PRIMARY_READ
        bool oldAmbientMask = sdfAmbientMaskActive;
        bool clipQuery = sdfCanTracePartsIndependently() && h * stepScale <= 0.15 * sdfProgramLayout.stepScale;
        sdfAmbientMaskActive = oldAmbientMask && clipQuery;
        sdfAmbientDistanceCeiling = clipQuery ? h * stepScale / sdfProgramLayout.stepScale : SDF_FAR_DISTANCE;
        float d = sdfDeScaleField(mapDistanceMasked(surfacePoint + (surfaceNormal * h), clipQuery ? instanceMaskBase : SDF_INSTANCE_MASK_ALL), stepScale);
        sdfAmbientDistanceCeiling = SDF_FAR_DISTANCE;
        sdfAmbientMaskActive = oldAmbientMask;
#else
        float d = sdfDeScaleField(mapDistanceMasked(surfacePoint + (surfaceNormal * h), instanceMaskBase), stepScale);
#endif

        sdfEvalCount += 1.0; // one of the three AO rungs

        // A distant rung cannot cancel a closer rung's contact occlusion.
        occlusion += (max(h - d, 0.0) * scale);
        scale *= 0.9025;
    }

    return clamp((1.0 - (5.07 * occlusion)), 0.0, 1.0);
}
// One-sample fleet approximation of calcAO. The middle quality rung (h=.07) captures the contact/crease signal. Its
// gain matches the three-rung ladder's constant-factor response: 5.07*(.01 + .9025*.07 + .9025^2*.13)/.07 = 12.97.
// It also matches the small-gap response within ~6%, retaining the grounding cue while removing two field walks.
float calcFastAO(float3 surfacePoint, float3 surfaceNormal, uint instanceMaskBase, float stepScale) {
    const float h = 0.07;
    float d = sdfDeScaleField(mapDistanceMasked(surfacePoint + (surfaceNormal * h), instanceMaskBase), stepScale);

    sdfEvalCount += 1.0; // the single fleet-tier AO tap

    return clamp((1.0 - (12.97 * (h - d))), 0.0, 1.0);
}
#endif
