// A previous hit proposes a start; only a current conservative field sample proves it safe.
// SurfaceEpsilon comes from sdf-march-constants.hlsli, shared with primary acceptance.
#ifndef PUCK_SDF_MARCH_SEED_HLSLI
#define PUCK_SDF_MARCH_SEED_HLSLI

struct SdfMarchSeed {
    float candidate;
    float midpoint;
    float radius;
};

// Back off two acceptance margins so a stationary front-facing plane can clear the proof ball.
// This proposal proves nothing about intervening geometry. A caller samples its current field at midpoint,
// then calls sdfMarchSeedClears. Invalid or nonprogressing proposals leave a neutral result.
bool sdfPrepareMarchSeed(float start, float projectedDistance, float pixelFootprint, out SdfMarchSeed seed) {
    seed = (SdfMarchSeed)0;
    if (!isfinite(start) || !isfinite(projectedDistance) || !isfinite(pixelFootprint) ||
        start < 0.0 || projectedDistance < 0.0 || pixelFootprint < 0.0) return false;

    precise float backoff = 2.0 * max(SurfaceEpsilon, pixelFootprint * projectedDistance);
    precise float candidate = projectedDistance - backoff;
    if (!isfinite(candidate) || candidate <= start) return false;

    precise float halfLength = (candidate - start) * 0.5;
    precise float midpoint = start + halfLength;
    // The endpoint has the interval's largest footprint. Expanding by that acceptance band protects near hits,
    // not just zero crossings, because every earlier footprint is no larger.
    // Rounding can move midpoint toward either endpoint. Measure both actual reaches, not nominal halfLength.
    precise float reach = max(midpoint - start, candidate - midpoint);
    precise float radius = reach + max(SurfaceEpsilon, pixelFootprint * candidate);
    if (!isfinite(midpoint) || !isfinite(radius)) return false;

    seed.candidate = candidate;
    seed.midpoint = midpoint;
    seed.radius = radius;
    return true;
}

// seed must come from successful sdfPrepareMarchSeed; a refused neutral result is not a proposal.
// clearance is the current field's positive lower bound at midpoint: already multiplied by the program StepScale
// and limited by the fold-safe step bound and the world-space wallpaper LOD gap. Never use abs(distance), raw unscaled
// distance, or scale it a second time.
// The ball contains the whole skipped interval and its acceptance band; equality does not prove it empty.
bool sdfMarchSeedClears(SdfMarchSeed seed, float clearance) {
    return isfinite(clearance) && clearance > 0.0 && clearance > seed.radius;
}

#endif
