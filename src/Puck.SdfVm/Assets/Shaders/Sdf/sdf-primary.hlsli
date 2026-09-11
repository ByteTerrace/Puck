#ifndef PUCK_SDF_PRIMARY_HLSLI
#define PUCK_SDF_PRIMARY_HLSLI

// Full-scene and independent whole-part queries share this marcher.
// Included after the world constants and VM helpers, before renderView.
struct SdfPrimaryHit {
    float traveled;
    float radius;
    float threshold;
    int material;
    float4 lanes;
    int frameSlot;
    float blendWeight;
    int blendOther;
    uint steps;
    bool found;
};

SdfHit sdfPrimarySample(float3 position, uint mask, uint4 part, bool localPart) {
#ifndef SDF_VM_DISABLE_PART_PROGRAMS
    if (localPart) {
        sdfMaterialBlendWeight = 0.0;
        sdfMaterialBlendOther = 0;
        sdfMapStepBound = SDF_STEP_BOUND_NONE;
        SdfHit hit;
        hit.distance = SDF_FAR_DISTANCE;
        hit.material = 0;
        hit.lanes = 0.0;
        hit.frameSlot = -1;
        sdfComposePartProgram(hit, position, part, sdfProgramLayout.dataOffset, true);
        hit.distance *= sdfProgramLayout.stepScale;
        return hit;
    }
#endif
    return mapMasked(position, mask);
}

SdfPrimaryHit sdfTracePrimaryField(float3 rayOrigin, float3 rayDirection, float marchStart,
    float firstExit, float secondEntry, float farBound, float farDistance, uint instanceMaskBase,
    float pixelFootprint, uint4 part, bool localPart) {
    float traveled = max(marchStart, 0.0);
    bool hitSurface = false;
    int material = 0;
    float4 hitLanes = 0.0;
    int hitFrameSlot = -1;
    float candidateMargin = SDF_FAR_DISTANCE;
    float candidateT = 0.0;
    float materialBlendWeight = 0.0;
    int materialBlendOther = 0;
    int marchStep = 0;
    float terminalRadius = 0.0;
    float terminalHitThreshold = SurfaceEpsilon;
    // Sphere-trace to the surface with a footprint-ADAPTIVE hit threshold. The field mapMasked returns is already
    // Lipschitz-clamped (SdfProgram stepScale; <= 1-Lipschitz along the ray), so over-relaxing stays
    // safe. DEFAULT: Bán & Valasek 2023 AUTO-RELAXED tracing — a per-ray slope EMA `slopeM` drives an adaptive
    // over-relaxation omega = max(1, 2/(1 - m)) (planar approach steps big, concave degenerates to a plain step),
    // with a disjoint-sphere step-back on overshoot. This subsumes the fixed clear-space multiplier the teleport
    // increment carried. STRICT (SDF_STRICT_MARCH): the plain omega=1.2 Keinert marcher — the conservative
    // cross-backend parity reference; the auto-relaxed step's division never rides the strict gate. The four-bound
    // teleport runs in BOTH paths.
    //
    // Footprint-hit biases (both conservative toward the camera — fatten a silhouette, never drop geometry):
    // (1) pixelFootprint * traveled is the pixel's full world DIAMETER (2x Keinert's radius); (2) `radius` is
    // Lipschitz-clamped, so the test fires at true distance threshold/stepScale.
#ifdef SDF_STRICT_MARCH
    float omega = SphereTraceOmega;
#else
    float slopeM = -1.0; // slope EMA, init -1 => the first step is plain (omega = 1)
#endif
    float previousRadius = 0.0;
    float stepLength = 0.0;

    [loop]
    for (marchStep = 0; (marchStep < MaxSteps); marchStep++) {
        SdfHit hit = sdfPrimarySample(rayOrigin + (rayDirection * traveled), instanceMaskBase, part, localPart);

        sdfEvalCount += 1.0; // one primary-march sample

        // FOLD-SAFE split: STEP (sizing, unbounding spheres, the slope EMA) on min(value, sdfMapStepBound) —
        // the sound marchable field near a fold boundary — but TERMINATE on the raw value (exact in the owning
        // cell; the bound never invents a phantom boundary hit). Fold-free programs: the min is the identity.
        float fieldDistance = hit.distance;
        float radius = min(fieldDistance, sdfMapStepBound);
        float hitThreshold = max(SurfaceEpsilon, (pixelFootprint * traveled));
        // The closest-approach candidate (see its declaration): every evaluated sample competes, INCLUDING one an
        // overshoot retreat is about to skip — that skipped sample is exactly the one the exhaustion arm exists
        // for. Recorded at this sample's own depth, before any retreat moves `traveled`.
        float margin = (fieldDistance - hitThreshold);

        if (margin < candidateMargin) {
            candidateMargin = margin;
            candidateT = traveled;
        }

        bool overshoot;

#ifdef SDF_STRICT_MARCH
        // Keinert over-relaxation: omega=1.2 with a disjoint-sphere step-back, latching to plain tracing (omega=1)
        // for the rest of the ray once it overshoots. Never terminate on an overshoot-retreat step.
        overshoot = ((omega > 1.0) && ((radius + previousRadius) < stepLength));

        if (overshoot) {
            stepLength -= (omega * stepLength);
            omega = 1.0;
        }
        else {
            stepLength = (radius * omega);
        }

        previousRadius = radius;
#else
        // Auto-relaxed step (Bán 2023). `stepLength` is the step that reached this sample. A disjoint-sphere
        // overshoot (`stepLength > |R| + r` — the over-relaxed step tunneled past / off the previous unbounding
        // sphere) is rejected: retreat to the previous sample and plain-step, resetting the slope. The divided
        // step and the fallback compare are `precise` so DXC's SPIR-V/DXIL FMA contraction can't flip the branch
        // near tangency; SlopeCap keeps (1 - m) away from 0 there.
        precise float sphereReach = (abs(radius) + previousRadius);
        overshoot = ((stepLength > 0.0) && (stepLength > sphereReach));

        if (overshoot) {
            traveled -= stepLength; // undo the unsafe step — back to the previous accepted sample
            radius = previousRadius;
            slopeM = -1.0;          // next step is plain (omega = 1)
        }
#endif

        // SHARED hit-accept: both march paths have now decided this sample's overshoot/step outcome and land
        // here — an overshoot-retreat sample is never tested (there is nothing new to accept this iteration),
        // and the coverage-AA epilogue's terminal-state capture (terminalRadius/terminalHitThreshold) lives in
        // ONE place instead of duplicated per path.
        if (!overshoot && (fieldDistance < hitThreshold)) {
            hitSurface = true;
            material = hit.material;
            hitLanes = hit.lanes;
            hitFrameSlot = hit.frameSlot;
            // This mapMasked() call evaluated at exactly surfacePoint (traveled is frozen at the break), so its
            // per-thread material blend channel describes THIS hit's winning smooth seam — capture it now, before the
            // epilogue's normal/AO/shadow marches overwrite the static.
            materialBlendWeight = sdfMaterialBlendWeight;
            materialBlendOther = sdfMaterialBlendOther;
            terminalRadius = fieldDistance;
            terminalHitThreshold = hitThreshold;
            break;
        }

        // The depth this step leaves from (after any retreat above) — the plain-step fallback below re-steps from it.
        float stepFrom = traveled;

#ifdef SDF_STRICT_MARCH
        traveled += stepLength;
#else
        // Update the slope EMA from the step that reached this sample (skip the very first sample; an
        // overshoot-retreat step already reset slopeM above). Only reached when the shared accept check did
        // not break.
        if (!overshoot && (stepLength > 0.0)) {
            precise float slope = ((radius - previousRadius) / stepLength);
            slopeM = lerp(slopeM, slope, SlopeBeta);
        }

        precise float denominator = (1.0 - min(slopeM, SlopeCap));
        precise float omega = max(1.0, (2.0 / denominator));
        precise float advance = (radius * omega);
        previousRadius = radius;
        stepLength = advance;
        traveled += stepLength;
#endif
        // A far exit may only be taken on a VALIDATED step. An over-relaxed step (omega > 1: stepLength > radius)
        // is not proven clear by the 1-Lipschitz bound — only the next sample's disjoint-sphere test can reject a
        // step that tunneled past a surface, and the two exits below fire before that sample exists. So a relaxed
        // step that would cross the far plane / far bound is retaken as the plain step (radius, omega = 1 — the
        // proven-clear advance; the slope resets as after a retreat), and the ray exits only if the plain step
        // crosses too. A ray whose relaxed step never crosses an exit is untouched, so silhouettes against a far
        // background — where omega reaches 2 / (1 - SlopeCap) = 10 while the ray accelerates away from the near
        // object — resolve the background instead of vaulting it into sky.
        if (((traveled >= farBound) || (traveled > farDistance)) && (stepLength > radius)) {
            stepLength = radius;
            traveled = (stepFrom + radius);
#ifndef SDF_STRICT_MARCH
            slopeM = -1.0; // the next step is plain — the same reset an overshoot retreat takes
#endif
        }

        // Four-bound teleport (Larsson "The Gunk"): once the ray marches past the tile's first occupied band without
        // converging, it is inside the beam-proven-empty gap — jump straight to the second band's start. secondEntry
        // >= firstExit, so this fires at most once (past secondEntry it is a no-op); a tile with no proven gap packs
        // firstExit = the far distance, making the branch dead. The teleport lands at secondEntry <= the ray's true
        // re-entry, so `traveled` — and the footprint threshold — is never inflated beyond a normal march (it cannot
        // worsen the ground-notch). Reset the relaxation state so a stale step/slope does not carry across the jump.
        if ((traveled >= firstExit) && (traveled < secondEntry)) {
            traveled = secondEntry;
            previousRadius = 0.0;
            stepLength = 0.0;
#ifndef SDF_STRICT_MARCH
            slopeM = -1.0;
#else
            // Strict keeps omega latched at 1 after an overshoot and across teleports.
            // Only previousRadius/stepLength reset, so the disjoint-sphere
            // step-back restarts cleanly at the landing sample without resurrecting over-relaxation the overshoot
            // already retired.
#endif
        }

        // F1 FAR-FIELD EXIT: past the tile's beam-proven far bound no ray in the tile can produce a hit the fine
        // march would ACCEPT (coneMarchFarBound proved it against the footprint-inflated threshold), so the ray
        // renders skyColor whether it exits here or marches on — OUTPUT-IDENTICAL, only fewer steps. farBound =
        // the far distance (no bound proven, or the A/B lever pushed it out of reach) makes this a no-op past the
        // far plane the far-distance break already handles. Both exits are reached only on a validated step (the
        // plain-step fallback above) or a cone-proven teleport landing.
        if (traveled >= farBound) {
            break;
        }

        if (traveled > farDistance) {
            break;
        }
    }

    // THE EXHAUSTION ARM — the ONE accept rule, re-applied to the closest approach. A ray that ended its budget
    // (the step cap, or a far exit) without the in-loop arm accepting a sample, but whose closest approach DID
    // satisfy fieldDistance < max(SurfaceEpsilon, footprint * t) (candidateMargin < 0 — only an overshoot-skipped
    // sample can be in that state, see the candidate's declaration), is a hit at that sample: re-evaluate the
    // field there (one extra eval on this rare path, so the loop carries no per-sample material/blend capture) and
    // shade with its material and normal exactly as the in-loop arm would have. A ray whose closest approach never
    // satisfied the rule stays a miss — there is no second, looser threshold here. Adjacent pixels along an edge
    // therefore resolve by the same rule whichever arm ends them.
    if (!hitSurface && (candidateMargin < 0.0)) {
        traveled = candidateT;

        SdfHit candidate = sdfPrimarySample(rayOrigin + (rayDirection * traveled), instanceMaskBase, part, localPart);

        sdfEvalCount += 1.0;
        hitSurface = true;
        material = candidate.material;
        hitLanes = candidate.lanes;
        hitFrameSlot = candidate.frameSlot;
        materialBlendWeight = sdfMaterialBlendWeight;
        materialBlendOther = sdfMaterialBlendOther;
        terminalRadius = candidate.distance;
        terminalHitThreshold = max(SurfaceEpsilon, (pixelFootprint * traveled));
    }

    SdfPrimaryHit result;
    result.traveled = traveled;
    result.radius = terminalRadius;
    result.threshold = terminalHitThreshold;
    result.material = material;
    result.lanes = hitLanes;
    result.frameSlot = hitFrameSlot;
    result.blendWeight = materialBlendWeight;
    result.blendOther = materialBlendOther;
    result.steps = (uint)marchStep;
    result.found = hitSurface;
    return result;
}

// Independent marches select geometry only. Keeping attributes out of this result lets the compiler
// discard their loop-carried state; the complete field resolves them once after nearest-hit selection.
struct SdfPrimarySurface {
    float traveled;
    float radius;
    float threshold;
    uint steps;
    bool found;
};

SdfPrimarySurface sdfTracePrimarySurface(float3 rayOrigin, float3 rayDirection, float marchStart,
    float firstExit, float secondEntry, float farBound, float farDistance, uint instanceMaskBase,
    float pixelFootprint, uint4 part, bool localPart) {
    SdfPrimaryHit hit = sdfTracePrimaryField(rayOrigin, rayDirection, marchStart, firstExit, secondEntry,
        farBound, farDistance, instanceMaskBase, pixelFootprint, part, localPart);
    SdfPrimarySurface surface;
    surface.traveled = hit.traveled;
    surface.radius = hit.radius;
    surface.threshold = hit.threshold;
    surface.steps = hit.steps;
    surface.found = hit.found;
    return surface;
}

SdfPrimaryHit sdfTracePrimary(float3 rayOrigin, float3 rayDirection, float marchStart,
    float firstExit, float secondEntry, float farBound, float farDistance, uint instanceMaskBase,
    float pixelFootprint) {
    bool independent = false;
#if defined(SDF_PRIMARY_PASS) && !defined(SDF_VM_DISABLE_PART_PROGRAMS)
    // Root composition admission is rebuilt with the program, not inferred from instruction counts or offsets.
    // Header .x: compiled count in bits 0..30, admission in bit 31. KEEP IN SYNC with SdfProgram.PartPrograms.cs.
    uint table = sdfProgramLayout.partProgramOffset;
    independent = table != 0u && (sdfWords[table].x & 0x80000000u) != 0u;
#endif
    if (!independent) {
        return sdfTracePrimaryField(rayOrigin, rayDirection, marchStart, firstExit, secondEntry,
            farBound, farDistance, instanceMaskBase, pixelFootprint, uint4(0u, 0u, 0u, 0u), false);
    }
    sdfPrimaryOmitParts = true;
    SdfPrimarySurface best = sdfTracePrimarySurface(rayOrigin, rayDirection, marchStart, firstExit, secondEntry,
        farBound, farDistance, instanceMaskBase, pixelFootprint, uint4(0u, 0u, 0u, 0u), false);

    // Enumerate candidates once per ray. Each local march evaluates a complete ordered part field,
    // including its cuts and smooth blends; an individual leaf never replaces its CSG parent.
    uint word = 0xFFFFFFFFu, bits = 0u, first, last, index;
    sdfNextVisibleInstanceRange(instanceMaskBase, sdfProgramLayout.instanceOffset, sdfProgramLayout.instanceCount,
        word, bits, first, last, index);
    [loop]
    while (first != SDF_SEGMENT_NONE) {
        uint4 part = sdfWords[sdfProgramLayout.partProgramOffset + 1u + index];
        bool ready = (part.z & 0x7FFFFFFFu) != 0u;
#ifndef SDF_DYNAMIC_TRANSFORMS
        ready = ready && (part.z & 0x80000000u) == 0u;
#endif
        if (ready) {
            float limit = min(farBound, farDistance);
            if (best.found) {
                limit = min(limit, best.traveled);
            }
            if (marchStart <= limit) {
                SdfPrimarySurface candidate = sdfTracePrimarySurface(rayOrigin, rayDirection, marchStart, firstExit,
                    secondEntry, limit, farDistance, instanceMaskBase, pixelFootprint, part, true);
                if (candidate.found && (!best.found || candidate.traveled < best.traveled)) {
                    best = candidate;
                }
            }
        }
        sdfNextVisibleInstanceRange(instanceMaskBase, sdfProgramLayout.instanceOffset, sdfProgramLayout.instanceCount,
            word, bits, first, last, index);
    }
    sdfPrimaryOmitParts = false;

    SdfPrimaryHit result = (SdfPrimaryHit)0;
    result.traveled = best.traveled;
    result.radius = best.radius;
    result.threshold = best.threshold;
    result.steps = best.steps;
    result.found = best.found;
    result.frameSlot = -1;
    if (best.found) {
        // Resolve attributes in original union order at the selected point, including ties and material seams.
        // Independent sampling can choose a different accepted point from the full-scene march.
        SdfHit resolved = mapMasked(rayOrigin + rayDirection * best.traveled, instanceMaskBase);
        sdfEvalCount += 1.0;
        result.radius = resolved.distance;
        result.threshold = max(SurfaceEpsilon, pixelFootprint * best.traveled);
        result.material = resolved.material;
        result.lanes = resolved.lanes;
        result.frameSlot = resolved.frameSlot;
        result.blendWeight = sdfMaterialBlendWeight;
        result.blendOther = sdfMaterialBlendOther;
    }
    return result;
}

#endif
