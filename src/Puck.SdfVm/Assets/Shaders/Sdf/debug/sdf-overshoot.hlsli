// The overshoot debug view's plain sphere march.
#ifndef DEBUG_SDF_OVERSHOOT_HLSLI
#define DEBUG_SDF_OVERSHOOT_HLSLI
// The overshoot detector's plain sphere march (DEBUG VIEW ONLY — never on the shipped shading path). Returns the
// terminal depth for a footprint-adaptive sphere trace of the tile-masked field, stepping by (radius * stepMultiplier):
// stepMultiplier = 1 marches the PRODUCTION Lipschitz-clamped field (mapMasked already bakes stepScale, so radius is the
// safe clamped distance), while 1/stepScale FORCES the clamp back to 1.0 — the step then rides the raw, possibly-non-1-
// Lipschitz field, so a twisted/warped program TUNNELS the thin geometry the clamp exists to hold. world.debug-view overshoot
// colors the two terminals' disagreement. Plain omega=1 (no auto-relaxation) so the ONLY variable between the two
// marches is the clamp; the four-bound teleport rides both (bound-proven on either). The hit ACCEPT compares the clamped
// radius against the same footprint threshold the production march uses — only the STEP is enlarged, so the enlarged
// step can jump PAST a surface before the sample reads a hit (the overshoot). Two full marches per pixel is the
// documented debug cost — the overshoot case gates the primary march OFF, so a pixel runs this twice and the production
// marcher zero times.
// With sampleOnce it returns the distance of its first sample, at marchStart, uncounted: the slice view's one read of the
// field, which runs through this same loop so the views kernel inlines the interpreter once for every debug field read.
float marchOvershootDepth(float3 rayOrigin, float3 rayDirection, float marchStart, float firstExit, float secondEntry, float farDistance, uint instanceMaskBase, float pixelFootprint, float stepMultiplier, bool sampleOnce) {
    if (marchStart < 0.0) {
        return farDistance; // a beam-culled tile — nothing to march; both marches agree at the far plane
    }

    float traveled = max(marchStart, 0.0);

    [loop]
    for (int step = 0; (step < MaxSteps); step++) {
        float radius = mapDistanceMasked(rayOrigin + (rayDirection * traveled), instanceMaskBase);

        if (sampleOnce) {
            return radius;
        }

        sdfWorkSteps += 1u;
        float hitThreshold = max(SurfaceEpsilon, (pixelFootprint * traveled));

        // Accept on the CLAMPED field (production-consistent), so a landed-inside sample (radius < threshold, incl.
        // negative) ends the march before any backward step. Tunneling happens when the enlarged step below clears the
        // thin band so no sample ever lands within the threshold inside it.
        if (radius < hitThreshold) {
            break;
        }

        // FOLD-SAFE: both detector marches cross every fold wall as the shipped marches do, so the ONLY remaining
        // variable between them stays the Lipschitz clamp (the detector's purpose).
        float advance = (radius * stepMultiplier);
        bool proven;
        float switchAt;

        traveled = sdfMarchAdvance(rayOrigin, rayDirection, traveled, advance, advance, (0.5 * hitThreshold), farDistance, true, proven, switchAt);

        // The four-bound teleport (bound-proven for either march): jump the proven-empty gap once.
        if ((traveled >= firstExit) && (traveled < secondEntry)) {
            traveled = secondEntry;
        }

        // Plain omega = 1 steps are provably clear by the 1-Lipschitz bound, so crossing the far plane here IS a
        // validated escape (unlike the primary march's over-relaxed step, which must fall back to the plain step first).
        if (traveled > farDistance) {
            traveled = farDistance;
            break;
        }
    }

    return traveled;
}

#endif
