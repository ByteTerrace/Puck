// The overshoot debug view's plain sphere march.
#ifndef DEBUG_SDF_OVERSHOOT_HLSLI
#define DEBUG_SDF_OVERSHOOT_HLSLI
// The overshoot detector's plain sphere march (DEBUG VIEW ONLY — never on the shipped shading path). It finds the
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
// With sampleOnce its depth is the distance of its first sample, at marchStart, uncounted: the slice view's one read of
// the field, which runs through this same march so the views kernel inlines the interpreter once for every field read.
// The march is resumable: its owner asks it for each sample (sdfOvershootSample) and hands it the sample's distance
// (sdfOvershootTake), so the owner's one field call site serves it beside the kernel's other reads.
struct SdfOvershootMarch {
    float3 rayOrigin;
    float3 rayDirection;
    float firstExit;
    float secondEntry;
    float farDistance;
    uint instanceMaskBase;
    float pixelFootprint;
    float stepMultiplier;
    bool sampleOnce;
    float traveled;
    int step;
    bool done;
    float depth;
};

SdfOvershootMarch sdfOvershootBegin(float3 rayOrigin, float3 rayDirection, float marchStart, float firstExit, float secondEntry, float farDistance, uint instanceMaskBase, float pixelFootprint, float stepMultiplier, bool sampleOnce) {
    SdfOvershootMarch march;
    march.rayOrigin = rayOrigin;
    march.rayDirection = rayDirection;
    march.firstExit = firstExit;
    march.secondEntry = secondEntry;
    march.farDistance = farDistance;
    march.instanceMaskBase = instanceMaskBase;
    march.pixelFootprint = pixelFootprint;
    march.stepMultiplier = stepMultiplier;
    march.sampleOnce = sampleOnce;
    march.traveled = max(marchStart, 0.0);
    march.step = 0;
    march.done = false;
    march.depth = farDistance;

    if (marchStart < 0.0) {
        march.done = true; // a beam-culled tile — nothing to march; both marches agree at the far plane
    } else if (MaxSteps <= 0) {
        march.done = true;
        march.depth = march.traveled;
    }

    return march;
}

// The next sample the march takes, false once it has its depth.
bool sdfOvershootSample(SdfOvershootMarch march, out float3 at) {
    at = (march.rayOrigin + (march.rayDirection * march.traveled));

    return !march.done;
}

void sdfOvershootTake(inout SdfOvershootMarch march, float radius) {
    if (march.sampleOnce) {
        march.depth = radius;
        march.done = true;
        return;
    }

    sdfWorkSteps += 1u;
    float hitThreshold = max(SurfaceEpsilon, (march.pixelFootprint * march.traveled));

    // Accept on the CLAMPED field (production-consistent), so a landed-inside sample (radius < threshold, incl.
    // negative) ends the march before any backward step. Tunneling happens when the enlarged step below clears the
    // thin band so no sample ever lands within the threshold inside it.
    if (radius < hitThreshold) {
        march.depth = march.traveled;
        march.done = true;
        return;
    }

    // FOLD-SAFE: both detector marches cross every fold wall as the shipped marches do, so the ONLY remaining
    // variable between them stays the Lipschitz clamp (the detector's purpose).
    float advance = (radius * march.stepMultiplier);
    bool proven;
    float switchAt;

    march.traveled = sdfMarchAdvance(march.rayOrigin, march.rayDirection, march.traveled, advance, advance, (0.5 * hitThreshold), march.farDistance, proven, switchAt);

    // The four-bound teleport (bound-proven for either march): jump the proven-empty gap once.
    if ((march.traveled >= march.firstExit) && (march.traveled < march.secondEntry)) {
        march.traveled = march.secondEntry;
    }

    // Plain omega = 1 steps are provably clear by the 1-Lipschitz bound, so crossing the far plane here IS a
    // validated escape (unlike the primary march's over-relaxed step, which must fall back to the plain step first).
    if (march.traveled > march.farDistance) {
        march.traveled = march.farDistance;
        march.depth = march.traveled;
        march.done = true;
        return;
    }

    march.step++;
    if (march.step >= MaxSteps) {
        march.depth = march.traveled;
        march.done = true;
    }
}

#endif
