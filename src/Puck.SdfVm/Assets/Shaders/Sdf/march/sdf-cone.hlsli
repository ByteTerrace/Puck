// The camera ray, the tile cone and its march bounds, and the per-tile instance masks.
#ifndef MARCH_SDF_CONE_HLSLI
#define MARCH_SDF_CONE_HLSLI
// The perspective ray for a viewport-local UV (pixel centers in [0,1] within the viewport's region; screen-up maps
// to the camera's +up). SYMMETRIC by construction: `direction`'s defining expression below is untouched from before
// the off-axis branch existed, so a camera that never sets lens.yz (every camera but a border window) takes
// the IDENTICAL sum in the IDENTICAL order — bit-exact, not merely numerically equal, which is what a build with the
// branch not taken needs to prove byte-identity against a build without it at all.
float3 cameraRayDirection(ViewportData view, float2 localUv) {
    float2 ndc = ((localUv * 2.0) - 1.0);

    ndc.y = -ndc.y;

    float tanHalfFov = view.right.w;
    float aspect = view.up.w;

    float3 direction = (
        view.forward.xyz +
        (((ndc.x * aspect) * tanHalfFov) * view.right.xyz) +
        ((ndc.y * tanHalfFov) * view.up.xyz)
    );

    // Off-axis (asymmetric) frustum shear for a border window (SdfAsymmetricFrustum, Puck.SdfVm.Views): the lens
    // row's two otherwise-zero lanes carry the frustum's tangent-space center offset, appended as a TRAILING
    // term so the symmetric sum above is never reassociated (float addition is not associative — computing the
    // offset into a fresh accumulator first, then adding, can round differently than one flat left-to-right sum).
    if ((view.lens.y != 0.0) || (view.lens.z != 0.0)) {
        direction += ((view.lens.y * view.right.xyz) + (view.lens.z * view.up.xyz));
    }

    return normalize(direction);
}

struct TileCone {
    float3 centerDirection;
    float chord;
    float inverseAperture; // 1/sqrt(1 - chord^2), the exact sphere-vs-cone bound's correction (see collectInstanceMaskWord)
};

TileCone buildTileCone(ViewportData view, float2 localUvMin, float2 localUvMax) {
    TileCone cone;

    cone.centerDirection = cameraRayDirection(view, (0.5 * (localUvMin + localUvMax)));
    cone.chord = 0.0;
    cone.chord = max(cone.chord, length(cameraRayDirection(view, localUvMin) - cone.centerDirection));
    cone.chord = max(cone.chord, length(cameraRayDirection(view, float2(localUvMax.x, localUvMin.y)) - cone.centerDirection));
    cone.chord = max(cone.chord, length(cameraRayDirection(view, float2(localUvMin.x, localUvMax.y)) - cone.centerDirection));
    cone.chord = max(cone.chord, length(cameraRayDirection(view, localUvMax) - cone.centerDirection));
    // Once per tile, not once per instance. The max() guards a degenerate wide cone; a 16 px tile's chord is ~0.02
    // (fullscreen) to ~0.044 (a 2x2 quad viewport), so this lands just above 1.
    cone.inverseAperture = rsqrt(max((1.0 - (cone.chord * cone.chord)), 1.0e-6));

    return cone;
}

// The four-bound teleport's per-tile output (Larsson "The Gunk"). `entry` is the classic conservative-cone marchStart
// (the earliest t at which any ray in the tile could hit — a march-start lower bound shared by every pixel in the tile,
// or TileEmpty when the cone clears the field out to the far distance), so plane 0 — and therefore the cull-args bbox,
// the compositor's empty-tile test, and the footprint-adaptive termination the ground-notch rides — is the exact
// classic beam output. `firstExit`/`secondEntry` bound ONE proven-empty gap the cone cleared between two occupied
// bands; when no gap is proven, firstExit = the far distance so the consumer's teleport is a total no-op (a total
// function, per the determinism pin). secondEntry >= firstExit always. `farBound` (F1, plane 3) is the depth past
// which the tile's cone cannot produce ANY footprint-accepted hit through the far distance (proven against the
// FOOTPRINT-INFLATED threshold — see coneMarchFarBound); the far distance when the tail phase proved no such bound (the
// consumer's far-exit is then a no-op). Every "nothing proven" sentinel is the view's worldFarDistance, never a constant.
struct TileBounds {
    float entry;
    float firstExit;
    float secondEntry;
    float farBound;
};

// F1 TAIL PHASE. Proves the FAR BOUND: the depth past which the tile's cone cannot produce any
// hit the fine march would ACCEPT, all the way to the far distance. Marches forward from `startT` with a FOOTPRINT-INFLATED
// clearance threshold — the load-bearing correctness fact. The fine march accepts a hit at fieldDistance <
// max(SurfaceEpsilon, footprint*t) (sdf-world-views computes footprint = 2*right.w/rectDims.y; the beam computes the
// identical value from regionSizePx), and footprint*t ~ 0.001*t exceeds ConeEpsilon past t~2 — so a bare ConeEpsilon
// proof is ANTI-conservative and could bound above a real footprint hit. Inflating the cone's transverse radius by the
// pixel footprint (spread = chord + footprint) and requiring clearance = min(map(center), sdfMapStepBound) -
// spread*t > SurfaceEpsilon (stepping by clearance/(1 + spread), the 1-Lipschitz cone guarantee for the inflated cone)
// guarantees that for every ray and every t' in [farBound, farDistance] the hit-accept fieldDistance <
// max(SurfaceEpsilon, footprint*t') can NEVER fire — so the ray renders skyColor whether it exits at farBound or marches
// on, i.e. the far exit is OUTPUT-IDENTICAL on the shipped shading path (only step counts and the termination debug view
// change). The same rule is what the primary march's exhaustion arm accepts a closest-approach candidate against, so the proof
// covers that arm too. FOLD-SAFE like the gap phases (the bounded clearance rides sdfMapStepBound). Total function: no
// proven clear-to-far span within the budget => the far distance.
float coneMarchFarBound(ViewportData view, TileCone cone, uint instanceMaskBase, float footprint, float startT) {
    float3 origin = view.position.xyz;
    float farDistance = worldFarDistance(view);
    float spread = (cone.chord + footprint); // the cone half-spread inflated by the pixel footprint
    float t = startT;
    float clearStart = farDistance; // start of the CURRENT footprint-clear span (farDistance = not in one)
    bool clear = false;

    [loop]
    for (int i = 0; (i < TileFarSteps); i++) {
        if (t > farDistance) {
            // Reached the far plane. If we are inside a footprint-clear span, it extends to the far distance => the
            // far bound is that span's start; otherwise no bound was proven.
            return (clear ? clearStart : farDistance);
        }

        float clearance = (min(mapDistanceMasked(origin + (cone.centerDirection * t), instanceMaskBase), sdfMapStepBound) - (spread * t));
        sdfWorkSteps += 1u;

        if (clearance > SurfaceEpsilon) {
            if (!clear) {
                clearStart = t; // start of a proven footprint-clear span
                clear = true;
            }

            t += (clearance / (1.0 + spread)); // conservative step across the inflated-clear span
        }
        else {
            // Footprint geometry (or its cone margin) is present here — any earlier clear span does NOT reach the far
            // plane. Advance through the band (magnitude-stepped, floored); overshoot only shrinks/misses a far bound,
            // never invents one (the same one-sided safety the gap search's through-band phase relies on).
            clear = false;
            clearStart = farDistance;
            t += (max(-clearance, TileGapMinStep) / (1.0 + spread));
        }
    }

    return farDistance; // budget exhausted without proving a clear-to-far span => no far bound
}

// Cone march that additionally records the first proven-empty gap past the entry band. The GAP is
// conservative for the WHOLE tile cone: `firstExit` is a t at which the cone clearance is strictly positive (every
// ray in the tile is clear there) and the search then steps by <= clearance/(1+chord) — the sphere-trace guarantee —
// so it cannot skip the cone re-entering geometry; the first re-entry is `secondEntry`. Overstepping the interior of
// the first band (the through-band phase) can only MISS a gap (reporting firstExit = the far distance), never invent
// one, so a teleport is never unsafe. Reaching the far distance while clear yields secondEntry = the far distance (an
// empty tail — the ray teleports to the far plane and ends), the one far-bound benefit taken here.
//
// The tile's instance mask excludes only bounds with no influence on its cone; world segments always evaluate.
// Independently traced parts use a short entry search: the full-scene gap/tail searches cost more than the
// cheaper local marches save. Exhaustion leaves a proven-clear start, never an empty tile or invented far bound.
// Other root compositions retain the full entry/gap/tail search below.
TileBounds coneMarchTileBounds(ViewportData view, TileCone cone, uint instanceMaskBase, float footprint) {
    float3 origin = view.position.xyz;
    float farDistance = worldFarDistance(view);
    bool entryOnly = sdfCanTracePartsIndependently();

    TileBounds b;
    b.entry = TileEmpty;
    b.firstExit = farDistance;   // no proven gap => teleport disabled (total function)
    b.secondEntry = farDistance;
    b.farBound = farDistance;    // F1: no proven far bound yet => the consumer's far-exit is a no-op (total function)

    // ENTRY: the Lipschitz-clamped field clears the cone by map(center) - chord*t.
    // Advancing by clearance/(1+chord) stays conservative, including when the entry budget ends early.
    float t = worldSurfaceNearDistance(view);
    bool foundEntry = false;
    int entrySteps = entryOnly ? IndependentConeMarchSteps : ConeMarchSteps;

    [loop]
    for (int i = 0; (i < entrySteps); i++) {
        // FOLD-SAFE: the clearance proof rides min(value, sdfMapStepBound). A folded field's raw value can
        // overestimate near a fold boundary, and a cone proof built on it classifies tiles straight through shell
        // geometry (the Droste tile-shatter). min with the published boundary gap
        // is an honest unbounding sphere of the TRUE field, so entry, TileEmpty, and the gap proofs stay sound; a
        // fold-free program publishes SDF_STEP_BOUND_NONE and this min is the identity.
        float clearance = (min(mapDistanceMasked(origin + (cone.centerDirection * t), instanceMaskBase), sdfMapStepBound) - (cone.chord * t));
        sdfWorkSteps += 1u;

        if (clearance <= ConeEpsilon) {
            b.entry = t;
            foundEntry = true;
            break;
        }

        t += (clearance / (1.0 + cone.chord));

        if (t > farDistance) {
            return b; // TileEmpty entry, no gap — cull-args drops the tile
        }
    }

    if (entryOnly) {
        if (!foundEntry) b.entry = t;
        return b; // Gap and far-bound sentinels leave all remaining work to primary rays.
    }
    if (!foundEntry) {
        b.entry = t; // step budget exhausted at the entry band — matches coneMarchTile's fallthrough `return t`
        // F1 leak #2: a budget-exhausted grazing tile is marked LIVE (all its pixels fine-march from t). Prove the far
        // bound from here so sky pixels that clear the near band exit early instead of running to the far distance.
        b.farBound = coneMarchFarBound(view, cone, instanceMaskBase, footprint, t);

        return b;
    }

    // Phases 2/3 — walk PAST the entry band to prove one empty gap. `clear` flips true once the cone is provably
    // clear again (firstExit); the first time it dips back under ConeEpsilon after that is secondEntry.
    bool clear = false;
    int stall = 0;                    // consecutive in-band, non-increasing-clearance steps (the early-abandon streak)
    float previousClearance = 1.0e20; // seeded large so the first in-band step counts as non-increasing

    [loop]
    for (int j = 0; (j < TileGapSteps); j++) {
        // FOLD-SAFE, same as phase 1: a raw-value overestimate here would prove a FALSE clear span across a fold
        // boundary — an unsafe teleport. The bounded clearance keeps firstExit/secondEntry honest.
        float clearance = (min(mapDistanceMasked(origin + (cone.centerDirection * t), instanceMaskBase), sdfMapStepBound) - (cone.chord * t));
        sdfWorkSteps += 1u;

        if (!clear) {
            if (clearance > ConeEpsilon) {
                b.firstExit = t;               // start of a proven-clear span
                clear = true;
                t += (clearance / (1.0 + cone.chord)); // conservative step within the clear span
            }
            else {
                // Still inside/near the entry band. Early-abandon a cone that is only descending deeper (a ground/wall
                // tile with no gap): count consecutive non-increasing in-band clearances and give up once the streak
                // hits TileGapStallLimit. A real gap's cone re-clears within a few magnitude-stepped steps, resetting
                // the streak first; abandoning a non-gap tile only skips an unproven teleport (pixel-identical).
                stall = ((clearance <= previousClearance) ? (stall + 1) : 0);

                if (stall >= TileGapStallLimit) {
                    b.firstExit = farDistance;
                    b.secondEntry = farDistance;
                    // Stop the whole search after the descending-band stall. No gap or tail has been proven, so
                    // keep the initialized far bound at farDistance; never infer empty space from the stall itself.

                    return b;
                }

                // advance through the band (magnitude-stepped, floored so a near-zero clearance can't stall).
                // Overshooting the exit only shrinks/misses a gap, never invents one.
                t += (max(-clearance, TileGapMinStep) / (1.0 + cone.chord));
            }

            previousClearance = clearance;
        }
        else {
            if (clearance <= ConeEpsilon) {
                b.secondEntry = t;             // cone re-enters geometry — gap = [firstExit, secondEntry]
                // F1 leak #4: past the ONE proven gap there was no far information. Prove the far bound from the second
                // band so a ray that clears it exits early instead of marching the second band's sky to the far distance.
                b.farBound = coneMarchFarBound(view, cone, instanceMaskBase, footprint, t);

                return b; // one gap is the 90% win (Larsson clamps to one re-entry)
            }

            t += (clearance / (1.0 + cone.chord)); // stay conservative across the clear span
        }

        if (t > farDistance) {
            if (clear) {
                b.secondEntry = farDistance;   // proven clear to the far plane — teleport ends the ray (the far bound)
            }
            else {
                b.firstExit = farDistance;     // never cleanly exited the band — disable the teleport
            }

            return b;
        }
    }

    // Budget exhausted without a clean second entry: we cannot prove the span past firstExit stays empty, so DISABLE
    // the teleport (stay conservative — never teleport past unproven space).
    b.firstExit = farDistance;
    b.secondEntry = farDistance;
    // F1: the gap budget ran out mid-band; the tail phase gets its own budget to prove a far bound from here.
    b.farBound = coneMarchFarBound(view, cone, instanceMaskBase, footprint, t);

    return b;
}

// Per-tile instance cull (the beam prepass, world path only — requires SDF_DYNAMIC_TRANSFORMS for a DYNAMIC
// instance's bound to resolve): tests the 32 instances of one mask WORD's index range against the tile's cone (the
// same center ray + chord coneMarchTile already computed) and sets an instance's bit when its world-space bounding
// sphere may be visible to ANY ray in the tile — the beam kernel calls this once per derived mask word
// (sdfInstanceMaskWordCount) and writes each word straight to the mask buffer. `instanceOffset` is the instance
// directory's offset (sdfInstanceDirectoryOffset), resolved ONCE by the caller so the per-instance bound loads skip
// the loop-invariant offset chain.
//
// THE BOUND. A ray of the tile satisfies |d_i - d0| <= chord = c, so a hit at parameter t requires the CENTER ray to
// pass within (r + c*t) of the sphere: |p(t) - C| <= r + c*t for some t >= 0. With a = dot(C - o, d0) and h the
// distance from C to the center-ray line, that is
//     g(t) = (1 - c^2)t^2 - 2(a + rc)t + (a^2 + h^2 - r^2) <= 0
// whose minimum over t sits at t* = (a + rc)/(1 - c^2) — NOT at t = a — and yields, after the numerator collapses to
// (r + ac)^2, the exact necessary condition
//     h <= (r + c*a) / sqrt(1 - c^2).
// Testing `h <= r + c*a` alone evaluates g at t = a only, so it CULLS SPHERES A REAL TILE RAY GRAZES. Measured: at a
// 2x2 quad viewport (chord ~0.044) a sphere tangent to a corner ray at t = 60 is rejected by up to 0.0028 world units,
// well past the host's ~0.0011 bound inflation (SdfProgram.BoundRadiusPadding/Scale). inverseAperture >= 1, so the
// inverseAperture >= 1, so the exact test is conservative.
// The exact sphere-vs-tile-cone necessary condition (the bound derivation above), factored out so the flat per-instance
// loop AND the uniform-grid cell walk decide a bit by the IDENTICAL arithmetic — the grid mask is then a pure
// SUBSET-selection of the flat mask (it only ever tests FEWER instances, never by a different float rule), so with a
// conservative cone footprint it equals the flat mask exactly. A PARKED instance (a reserved-pool slot with no live
// content this rebuild) packs a negative-radius sentinel host-side (SdfProgram.ParkedBoundRadius): reject it with the
// single leading branch — no sqrt, no dot, mask bit left 0. A real bound radius is always non-negative, so this never
// misfires.
bool sdfInstancePassesTileCone(float4 bound, float3 rayOrigin, float3 centerDirection, float chord, float inverseAperture) {
    if (bound.w < 0.0) {
        return false;
    }

    float3 toCenter = (bound.xyz - rayOrigin);
    float alongRay = max(dot(toCenter, centerDirection), 0.0);
    float axisDistance = length(toCenter - (centerDirection * alongRay));

    return (axisDistance <= ((bound.w + (chord * alongRay)) * inverseAperture));
}

uint collectInstanceMaskWord(uint instanceOffset, uint wordIndex, uint instanceCount, float3 rayOrigin, float3 centerDirection, float chord, float inverseAperture) {
    uint bits = 0u;
    uint first = (wordIndex << 5u);
    uint end = min((first + 32u), instanceCount);

    [loop]
    for (uint i = first; (i < end); i++) {
        float4 bound = sdfInstanceBoundAt(instanceOffset, i);

        if (sdfInstancePassesTileCone(bound, rayOrigin, centerDirection, chord, inverseAperture) && !sdfInstanceCameraHidden(instanceOffset, i)) {
            bits |= (1u << (i - first));
        }
    }

    return bits;
}

// The uniform-grid CELL WALK lives in sdf-instance-cull.comp.hlsl (collectInstanceGridMask): it writes mask bits
// straight into the per-tile mask buffer (each tile's words are exclusively owned by ONE invocation, so a same-thread
// read-modify-write is race-free), which only that kernel binds writable. Two rejected shapes, both MEASURED worse on
// the 4096-instance sweep: (a) a per-thread accumulation array — a dynamically indexed uint[SDF_MAX_INSTANCES/32]
// local allocates 512 B of thread scratch per invocation; (b) fusing the cull into sdf-beam — the walk's register
// high-water mark cost the co-resident cone march ~12% occupancy on BOTH paths (grid enabled or not). Hence the
// dedicated pass.

#endif
