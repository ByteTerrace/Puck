// The debug views: each numbered mode replaces the view's color with a diagnostic of the pixel's surface sample, its
// tile or its field. Mode 0 and every mode from DebugViewModeCount on keep the final shading, and the evals heatmap reads
// the query tally the final shading kept. KEEP IN SYNC with DebugViewModes.Names (src/Puck.SdfVm/DebugViewModes.cs), whose
// order is the pass block's debugMode.
#ifndef DEBUG_SDF_DEBUG_VIEWS_HLSLI
#define DEBUG_SDF_DEBUG_VIEWS_HLSLI
#ifdef SDF_VIEWS_PASS
#include "../frame/sdf-reprojection.hlsli"
#include "../indirect/sdf-indirect-read.hlsli"
#include "../indirect/sdf-indirect-light.hlsli"

// A distinct, stable hue per material id (an HSV hue ramp), not the table albedo — so id boundaries read clearly
// in the material-id debug view.
float3 materialPalette(int material) {
    float hue = frac(float(material) * 0.61803399);
    float3 ramp = (abs((frac(hue + float3(0.0, 0.33333333, 0.66666667)) * 6.0) - 3.0) - 1.0);

    return saturate(ramp);
}

float3 sdfDebugView(SdfPixel p, SdfSurfaceSample s, float3 color) {
    float3 viewColor = color;

    // The slice's plane (case 7). Default plane: through the WORLD ORIGIN with normal = camera forward (the debug subject
    // sits at the origin — a camera-locked slice). The pass block's slice axis and offset optionally select a world-axis
    // plane instead (the `sdf.slice` verb; camera-locked while the axis is 0).
    bool slicing = (p.viewMode == 7);
    bool sliceParallel = false;
    float planeT = 0.0;

    if (slicing) {
        float3 sliceNormal = p.view.forward.xyz; // already unit (the camera basis)
        float planeOffset = 0.0;               // the plane is dot(p, n) = planeOffset
        int sliceAxis = (int)round(passGroup.debugSliceAxis);

        if (sliceAxis == 1) { sliceNormal = float3(1.0, 0.0, 0.0); planeOffset = passGroup.debugSliceOffset; }
        else if (sliceAxis == 2) { sliceNormal = float3(0.0, 1.0, 0.0); planeOffset = passGroup.debugSliceOffset; }
        else if (sliceAxis == 3) { sliceNormal = float3(0.0, 0.0, 1.0); planeOffset = passGroup.debugSliceOffset; }

        float denominator = dot(p.rayDirection, sliceNormal);

        sliceParallel = (abs(denominator) < 1.0e-4);

        if (!sliceParallel) {
            planeT = ((planeOffset - dot(p.rayOrigin, sliceNormal)) / denominator);
        }
    }

    // The debug views' field reads run through one loop, so the views kernel inlines the interpreter once for all of
    // them: the slice's one sample of the UNMASKED field at its plane (case 7), and the overshoot detector's two marches,
    // clamped then unclamped (case 9).
    float firstRead = 0.0;
    float secondRead = 0.0;
    uint reads = ((p.viewMode == 9) ? 2u : ((slicing && !sliceParallel && !(planeT < 0.0)) ? 1u : 0u));

    [loop]
    for (uint read = 0u; (read < reads); read++) {
        float value = marchOvershootDepth(p.rayOrigin, p.rayDirection, (slicing ? planeT : p.marchStart), p.firstExit, p.secondEntry, p.farDistance, (slicing ? SDF_INSTANCE_MASK_ALL : p.instanceMaskBase), p.pixelFootprint, ((read == 0u) ? 1.0 : (1.0 / sdfStepScale())), slicing);

        if (read == 0u) {
            firstRead = value;
        } else {
            secondRead = value;
        }
    }

    switch (p.viewMode) {
        case DebugViewModeIndirect: {
            viewColor = sdfIndirectReceiverTotal;
            break;
        }
        case 1: { // depth
            float depth = saturate(s.t / p.farDistance);
            viewColor = float3(depth, depth, depth);
            break;
        }
        case 2: { // surface normals
            viewColor = (s.hit ? ((s.normal * 0.5) + 0.5) : float3(0.0, 0.0, 0.0));
            break;
        }
        case 3: { // ray direction
            viewColor = ((p.rayDirection * 0.5) + 0.5);
            break;
        }
        case 4: { // material id palette
            viewColor = (s.hit ? materialPalette(s.material) : float3(0.0, 0.0, 0.0));
            break;
        }
        case 5: { // iteration count ramp (after the cull fast-forward — empty tiles read ~0)
            float ramp = (float(s.steps) / float(MaxSteps));
            viewColor = float3(ramp, ramp, ramp);
            break;
        }
        case 6: { // termination cause — WHY the march loop exited, per pixel (reconstructed from post-loop state so
                  // the hot non-debug path's codegen is untouched: no per-step tracking, just a read of the exit facts).
                  // green = epsilon-dominated hit, cyan = footprint-dominated hit (either arm — a closest-approach
                  // candidate accepted at exhaustion classifies by the same dominance test, because it satisfied the
                  // same rule), red = MaxSteps exhausted with no candidate inside the rule (the ground-notch
                  // hypothesis), dark blue = escaped (a validated step past the far distance or the F1 far bound, or a
                  // tile the beam culled empty). A break leaves marchStep below MaxSteps; only exhaustion reaches it.
                  //
                  // THE TERMINATION/SLICE SPLIT (deliberate, keep it): this view shows what the REAL pipeline does —
                  // tile cull included (a beam-culled tile reads as escaped/background here, because that is exactly
                  // what the production march would do). The SLICE view below shows the IDEAL field instead: the beam
                  // force-survives every tile for it and its evaluation is the UNMASKED map(), so no cull or mask can
                  // truncate the picture. One view diagnoses the pipeline, the other the mathematics.
            if (s.hit) {
                // Which term won the footprint-adaptive threshold at the hit: SurfaceEpsilon (near-camera precision
                // floor) or the pixel's world footprint (p.pixelFootprint * s.t). Same comparison the loop's
                // max(SurfaceEpsilon, p.pixelFootprint*s.t) made, read back at the hit distance.
                bool epsilonDominated = (SurfaceEpsilon >= (p.pixelFootprint * s.t));
                viewColor = (epsilonDominated ? float3(0.15, 0.90, 0.25) : float3(0.15, 0.80, 0.95));
            }
            else if ((p.marchStart < 0.0) || (s.steps < (uint)MaxSteps)) {
                viewColor = float3(0.02, 0.05, 0.28); // escaped to the sky (or a beam-culled empty tile) — background
            }
            else {
                viewColor = float3(0.92, 0.16, 0.10); // the loop ran out of steps without hitting or escaping
            }

            break;
        }
        case 7: { // distance-field cross-section — the IDEAL field, wall to wall (see the termination/slice split
                  // note on case 6). The march was skipped (the gate above); the beam force-survived every in-viewport
                  // tile for this mode, so every pixel of the viewport reaches here — no tile truncation, no staircase.
                  // The plane and its sample were read above the switch.
            if (sliceParallel) {
                viewColor = float3(0.0, 0.0, 0.0); // ray parallel to the slice — nothing to sample
                break;
            }

            if (planeT < 0.0) {
                viewColor = float3(0.0, 0.0, 0.0); // the plane is behind the camera along this ray
                break;
            }

            // The UNMASKED field (every instance, never the tile mask): the slice is the ideal mathematics, so no per-tile
            // instance mask may hide far-field contributions. Still the post-stepScale-clamp distance — the quantity the
            // marcher steps on — so an isoline IS a level set of the marched field.
            float sliceDistance = firstRead;

            // Two-scale isolines over the sign-split hue ramp (inside warm/red, outside cool/blue): brightness ramps
            // within each MINOR band (0.25 wu) so the gradient direction stays readable; a thin dark line marks every
            // minor boundary and a heavier, darker line every MAJOR band (1.0 wu), so distance reads at a glance
            // (count the heavy rings, then the light ones). The zero contour stays the one bright white line.
            const float MinorBand = 0.25;
            const float MajorBand = 1.0;
            float fieldMagnitude = abs(sliceDistance);
            float minorPhase = frac(fieldMagnitude / MinorBand);
            float majorPhase = frac(fieldMagnitude / MajorBand);
            float3 field = ((sliceDistance < 0.0) ? float3(0.90, 0.35, 0.22) : float3(0.22, 0.45, 0.90));
            float3 sliceColor = (field * (0.35 + (0.50 * minorPhase)));
            // Distance to the nearest band boundary, in band units (0 at a boundary, 0.5 mid-band).
            float minorEdge = min(minorPhase, (1.0 - minorPhase));
            float majorEdge = min(majorPhase, (1.0 - majorPhase));

            if (minorEdge < 0.05) {  // ~0.0125 wu half-width: thin dark minor line
                sliceColor *= 0.45;
            }

            if (majorEdge < 0.02) {  // ~0.02 wu half-width: heavier, near-black major line
                sliceColor *= 0.15;
            }

            if (fieldMagnitude < 0.02) {
                sliceColor = float3(1.0, 1.0, 1.0); // the bright zero contour — the cross-section outline wins over all
            }

            viewColor = sliceColor;
            break;
        }
        case 8: { // MASK DENSITY — tint by the kept-instance count in this pixel's tile (popcount over the tile's mask
                  // words), normalized by the live instance count. The counts are ALREADY in the mask buffer the views
                  // kernel binds (the beam prepass wrote them), so this is one popcount loop — no march, no field eval.
                  // Cull behaviour and tile-boundary artifacts become visible BY CONSTRUCTION: each tile's density is a
                  // single value, so adjacent tiles that kept different counts show a hard colour step. A world-only
                  // program (0 instances) reads 0 → the floor colour. This is how the lead WATCHES the storm cliff — a
                  // dense red field over the swarm means many instances survive the cull into each tile.
            uint liveInstances = sdfInstanceCount();
            uint keptInstances = 0u;

            [loop]
            for (uint maskWord = 0u; (maskWord < passGroup.instanceMaskWordCount); maskWord++) {
                keptInstances += countbits(sdfInstanceMaskWord(p.instanceMaskBase, maskWord, liveInstances));
            }

            // Fraction of the live instances this tile keeps. The sqrt lifts the low end so a handful of survivors out of
            // thousands still registers as green rather than washing to the floor blue — the ramp stays perceptible
            // across the whole range while the NORMALIZATION base stays the live count (as specified).
            float density = ((liveInstances > 0u) ? (float(keptInstances) / float(liveInstances)) : 0.0);
            float ramp = sqrt(saturate(density));
            // dark blue (0) -> green (low) -> red (high).
            float3 lowBand = lerp(float3(0.04, 0.07, 0.32), float3(0.14, 0.85, 0.30), saturate(ramp * 2.0));
            viewColor = lerp(lowBand, float3(0.95, 0.16, 0.10), saturate((ramp - 0.5) * 2.0));
            break;
        }
        case 9: { // OVERSHOOT DETECTOR — march the pixel TWICE and colour the depth disagreement. The first march is the
                  // production Lipschitz-CLAMPED field (stepMultiplier 1); the second forces the clamp to 1.0
                  // (stepMultiplier 1/stepScale) so the step rides the raw, possibly-non-1-Lipschitz field and TUNNELS
                  // thin geometry the clamp holds. Where they agree the clamp was not load-bearing (green); where the
                  // unclamped march tunneled past a surface the terminals diverge (hot) — the liar's-spiral class made
                  // live. This is a DEBUG-ONLY two-marches-per-pixel cost; the primary march was gated OFF above for it.
            // Both marches ran above the switch.
            float clampedDepth = firstRead;
            float unclampedDepth = secondRead;
            float disagreement = abs(clampedDepth - unclampedDepth);
            // Log-scaled against the march reach so a sub-unit tunnel still reads while a full escape saturates.
            float hot = saturate(log2(1.0 + disagreement) / log2(1.0 + p.farDistance));
            // green (agree) -> yellow -> red (the unclamped march tunneled far).
            float3 warmBand = lerp(float3(0.10, 0.70, 0.22), float3(0.98, 0.85, 0.12), saturate(hot * 2.0));
            viewColor = lerp(warmBand, float3(0.96, 0.12, 0.05), saturate((hot - 0.5) * 2.0));
            break;
        }
        case 10: { // EVALS — per-pixel HEATMAP of every map()-family field evaluation tallied this frame (primary
                   // march steps, soft-shadow march steps — regular or fast — the 3/1-tap AO ladder, the analytic-
                   // normal dual or its 4/5-tap fallbacks, and the coverage-AA open-space probe when taken). Unlike
                   // every other numbered mode this one runs the REAL final-shading epilogue (see useFinalShading
                   // above), so sdfEvalCount reflects actual per-frame cost, not a debug shortcut's own cost.
                   // Calibrated ramp (EvalHeatmapCeiling = 256, see its declaration for the worst-case budget this
                   // is sized against): dark blue (idle/background, 0 evals) -> green (a cheap ambient-only hit) ->
                   // yellow (a hit paying the soft-shadow march) -> red (256+, saturating so a runaway pixel reads
                   // solid red instead of wrapping).
            float evalRamp = saturate(sdfEvalCount / EvalHeatmapCeiling);
            float3 coldBand = lerp(float3(0.02, 0.04, 0.20), float3(0.14, 0.85, 0.30), saturate(evalRamp * 2.0));
            viewColor = lerp(coldBand, float3(0.95, 0.16, 0.10), saturate((evalRamp - 0.5) * 2.0));
            break;
        }
        case DebugViewModeIndirectProbes: {
            viewColor = sdfIndirectDebugProbes(p.rayOrigin, p.rayDirection, sdfIndirectDebugMaximum(s.hit, s.t, p.farDistance), color);
            break;
        }
        case DebugViewModeIndirectCells: {
            if (s.hit) {
                viewColor = sdfIndirectDebugCells(p.rayOrigin + p.rayDirection * s.t);
            }
            break;
        }
        case DebugViewModeIndirectLight: {
            if (s.hit && passGroup.shadowSlotCount > 0u && passGroup.shadowSlots.x >= 0) {
                uint lightIndex = (uint)passGroup.shadowSlots.x;
                uint region;
                float visible;
                bool fallback = !sdfIndirectLightLookup(lightIndex, p.rayOrigin + p.rayDirection * s.t, s.normal, visible, region);
                viewColor = fallback ? float3(visible, 0.0, 0.75) : float3(0.0, visible, 0.25 + 0.25 * (region % 2u));
            }
            break;
        }
        case 12: { // MOTION: red/green encode previous minus current pixel position at 1/32 per pixel;
                   // blue is one for valid reprojection. A still surface is (0.5, 0.5, 1), invalid history is black.
            float2 previousPixel;
            float previousT;
            bool valid = sdfReprojection(worldVisibilityRecord(p.pixel, p.viewIndex),
                p.rayOrigin + p.rayDirection * s.t, previousPixel, previousT);
            viewColor = (valid ? float3(saturate(0.5 + ((previousPixel - (float2(p.pixel) + 0.5)) / 32.0)), 1.0) : 0.0);
            break;
        }
        case 11: { // VISIBILITY — the kind of the pixel's current visibility record: background dark blue, SDF green,
                   // mesh orange.
            uint kind = SDF_VISIBILITY_KIND_BACKGROUND;

            if (worldVisibilityCurrent(p.pixel)) {
                kind = sdfVisibilityKind(sdfLoadVisibility(worldVisibilityRecord(p.pixel, p.viewIndex)).identity);
            }

            viewColor = ((kind == SDF_VISIBILITY_KIND_SDF)
                ? float3(0.15, 0.90, 0.25)
                : ((kind == SDF_VISIBILITY_KIND_MESH) ? float3(0.95, 0.55, 0.10) : float3(0.02, 0.05, 0.28)));
            break;
        }
    }

    return viewColor;
}

#endif
#endif
