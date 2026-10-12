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
// normal, where the clearance doubles every step. The advance crosses every fold wall through sdfMarchAdvance and the
// occlusion test reads the raw clearance in clamped units against the primary
// march's own accept threshold; the estimate divides a clearance by the world-unit distance travelled, so its
// clearance is de-scaled first (a clamped clearance would narrow a program's shadows with its stepScale bake).
// The stride's floor, ShadowStepMin, steps through an occluder thinner than it on either side of a log-sphere shell
// wall, but never through the wall itself: sdfMarchAdvance lands a stride that
// reaches a wall just past it, within the occlusion test's own threshold, and the march samples the other side there
// before striding on.
// The march takes the shadow-march quality and its light's penumbra sharpness; softShadowVisibility reads both
// from the view.
#ifdef SDF_QUERY_REJECTION_BOUND
// The clearance that proves a stride from traveled to the reach (stride's bound below): the estimate's level plus the
// stride and the estimate's growth over it. A query's running minimum starts here, so the clearance it reports, and
// its rejected bounds, prove as far as the field allows. Zero before the estimate starts.
float sdfShadowProofCeiling(float traveled, float reach, float stepScale, float visibility, float sharpness) {
    if (traveled < ShadowEstimateStart) { return 0.0; }
    return ((((stepScale * traveled) / sharpness) * visibility) +
        (max((reach - traveled), 0.0) * (1.0 + ((visibility * stepScale) / sharpness))));
}
#endif
float softShadowVisibilityMarch(float3 surfacePoint, float3 surfaceNormal, float3 lightDirection, uint instanceMaskBase, float stepScale, float reach,
    bool fastMarch, float sharpness) {
    int stepBudget = (fastMarch ? FastShadowSteps : ShadowSteps);
    float3 origin = (surfacePoint + (surfaceNormal * ShadowBias));
    float traveled = ShadowBias;
    float visibility = 1.0;

    reach = (fastMarch ? min(reach, FastShadowMaxDistance) : reach);
    // A clearance at least the step ceiling and at least visibility * traveled / sharpness (in de-scaled units, the
    // running estimate's own level) moves neither the stride nor the estimate's minimum, so the walk may stop looking
    // past it: each sample queries min(field, saturation) through
    // sdfQueryDistanceCeiling, which rejects every candidate farther away before its leaves run. The visibility, the
    // stride and the hit test are those of the full field bit for bit. Include the hit threshold too: an explicit scope
    // multiplier or a large local gradient can raise it above the step ceiling. KEEP IN SYNC with SdfShadowQuery.Ceiling.
    bool saturates = sdfCanTracePartsIndependently();
    float ceilingScale = (1.001 / sdfProgramLayout.stepScale);

    [loop]
    for (int step = 0; (step < stepBudget); step++) {
        float ceiling = (fastMarch
            ? max(FastShadowStepMax, (traveled * FastShadowStepFarSlope))
            : max(ShadowStepNear, (traveled * ShadowStepFarSlope)));
        float saturation = max(max(ceiling, SurfaceEpsilon * stepScale),
            ((traveled >= ShadowEstimateStart) ? (((stepScale * traveled) / sharpness) * visibility) : 0.0));
        float saturationCeiling = (saturates ? min(SDF_FAR_DISTANCE, saturation * ceilingScale) : SDF_FAR_DISTANCE);
#ifdef SDF_QUERY_REJECTION_BOUND
        // The walk rejects against the saturation, as before, so it evaluates exactly what it did; its running minimum
        // starts at the clearance a stride to the reach would need (sdfShadowProofCeiling), and every rejected bound
        // lowers sdfQueryRejectedBound. Their minimum is a lower bound of the field the stride below may take.
        sdfQueryRejectCeiling = saturationCeiling;
        sdfQueryDistanceCeiling = (saturates
            ? min(SDF_FAR_DISTANCE, max(saturation, sdfShadowProofCeiling(traveled, reach, stepScale, visibility, sharpness)) * ceilingScale)
            : SDF_FAR_DISTANCE);
        float bound = mapDistanceMasked(origin + (lightDirection * traveled), instanceMaskBase);
        sdfQueryRejectCeiling = SDF_FAR_DISTANCE;
        sdfQueryDistanceCeiling = SDF_FAR_DISTANCE;
        // The saturated walk's value bit for bit: rounding a product is monotone, so the minimum commutes with the
        // program's final multiplier.
        float clearance = min(bound, (saturationCeiling * sdfProgramLayout.stepScale));
        float provenClearance = (saturates ? min(bound, sdfQueryRejectedBound) : clearance);
#else
        sdfQueryDistanceCeiling = saturationCeiling;
        float clearance = mapDistanceMasked(origin + (lightDirection * traveled), instanceMaskBase);
        sdfQueryDistanceCeiling = SDF_FAR_DISTANCE;
        float provenClearance = clearance;
#endif

        sdfEvalCount += 1.0;
        sdfWorkSteps += 1u;

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

        // The ceiling keeps samples dense where they could lower the estimate. Past it the stride takes every step the
        // proven clearance c shows cannot: a point s further on reads at least c - s, so it neither hits while
        // s <= c - SurfaceEpsilon * stepScale nor lowers the estimate while k (c - s) / (stepScale (t + s)) stays at or
        // above the visibility, s <= (k c - v stepScale t) / (k + v stepScale).
        float stride = clamp(clearance, ShadowStepMin, ceiling);
        if (traveled >= ShadowEstimateStart) {
            float safe = min((provenClearance - (SurfaceEpsilon * stepScale)),
                (((sharpness * provenClearance) - (visibility * stepScale * traveled)) / (sharpness + (visibility * stepScale))));
            stride = max(stride, safe);
        }
        bool proven;
        float switchAt;

        traveled = sdfMarchAdvance(origin, lightDirection, traveled, stride, stride, ((0.5 * SurfaceEpsilon) * stepScale), reach, proven, switchAt);

        if (traveled > reach) {
            break;
        }
    }

    // A smoothstep so the penumbra's core and edge read as one soft band rather than a linear wedge.
    visibility = saturate(visibility);

    return ((visibility * visibility) * (3.0 - (2.0 * visibility)));
}
float softShadowVisibility(float3 surfacePoint, float3 surfaceNormal, float3 lightDirection, uint instanceMaskBase, float stepScale, float reach, int lightIndex) {
    return softShadowVisibilityMarch(surfacePoint, surfaceNormal, lightDirection, instanceMaskBase, stepScale, reach,
        worldUseFastSoftShadowMarch(), (1.0 / worldShadowPenumbraSlope(lightIndex)));
}
// Normal-ladder ambient occlusion (calcAO): from the hit, step a short ladder of fixed rungs OUTWARD along
// the surface normal; at each rung compare the distance expected to travel (h) against what the field actually reports
// (d) — where nearby geometry crowds the normal the field under-reports and the deficit (h - d) accumulates as
// occlusion, with an outer-rung falloff and a gain/clamp. THREE mapMasked() calls (was five): the rungs are
// re-spaced to span the SAME 0.01..0.13 reach at double pitch, the per-rung falloff squared (0.95^2 = 0.9025) to hold
// the same spatial decay, and the gain re-tuned 3.0 -> 5.07 so the fully-occluded floor matches the 5-tap value to
// ~0.01 (verified analytically over constant-factor and constant-gap occluder models) — a same-look AO at 60% of the
// taps. The rung loop is a [loop], NOT [unroll]: every mapDistanceMasked call site inlines a full copy of
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
//
// `fast` selects the one-sample fleet approximation. Its middle quality rung (h=.07) captures the contact/crease
// signal, and its gain matches the three-rung ladder's constant-factor response:
// 5.07*(.01 + .9025*.07 + .9025^2*.13)/.07 = 12.97. It also matches the small-gap response within ~6%, retaining the
// grounding cue while removing two field walks. Both forms share the ladder's one rolled field call site; the fast tap
// keeps its own literal rung, its unclipped mask and its unclamped deficit.
static const float AmbientOcclusionReach = 0.13;
float calcAO(float3 surfacePoint, float3 surfaceNormal, uint instanceMaskBase, float stepScale, bool fast) {
    float occlusion = 0.0;
    float scale = 1.0;
    int rungs = (fast ? 1 : 3);

    [loop]
    for (int i = 0; (i < rungs); i++) {
        float h = (fast ? 0.07 : (0.01 + (((AmbientOcclusionReach - 0.01) * float(i)) / 2.0)));
        uint queryMask = instanceMaskBase;
#ifdef SDF_PRIMARY_READ
        bool oldAmbientMask = sdfAmbientMaskActive;
        if (!fast) {
            bool clipQuery = sdfCanTracePartsIndependently() && h * stepScale <= 0.15 * sdfProgramLayout.stepScale;
            sdfAmbientMaskActive = oldAmbientMask && clipQuery;
            sdfQueryDistanceCeiling = clipQuery ? h * stepScale / sdfProgramLayout.stepScale : SDF_FAR_DISTANCE;
            queryMask = (clipQuery ? instanceMaskBase : SDF_INSTANCE_MASK_ALL);
        }
#endif
        float d = sdfDeScaleField(mapDistanceMasked(surfacePoint + (surfaceNormal * h), queryMask), stepScale);
#ifdef SDF_PRIMARY_READ
        if (!fast) {
            sdfQueryDistanceCeiling = SDF_FAR_DISTANCE;
            sdfAmbientMaskActive = oldAmbientMask;
        }
#endif

        sdfEvalCount += 1.0; // one AO rung, or the single fleet-tier tap
        sdfWorkSteps += 1u;

        if (fast) {
            return clamp((1.0 - (12.97 * (h - d))), 0.0, 1.0);
        }

        // A distant rung cannot cancel a closer rung's contact occlusion.
        occlusion += (max(h - d, 0.0) * scale);
        scale *= 0.9025;
    }

    return clamp((1.0 - (5.07 * occlusion)), 0.0, 1.0);
}
#endif
