using Puck.Maths;

namespace Puck.SignedDistance.Queries;

// What a March call proved. Miss and Hit are assertions about the ray; Exhausted is the absence of one — a radius
// cast lost its scaled clearance, a sample budget ran out, or the march reached a point the frame cannot express,
// with the field neither accepted nor cleared, so it proves NEITHER. Every consumer folds it into whichever of the two
// its own contract can survive being wrong about: Hit for an obstruction/contact question (Raycast/SphereCast/
// LineOfSight), Miss for TryGroundHeight, whose true half asserts a SURFACE.
internal enum MarchOutcome {
    Miss = 0,
    Hit = 1,
    Exhausted = 2,
}

// The field a march reads. A sample is either EXACT (the program's own value at the point) or a BOUND (any value at
// or below the exact one, never above it), and the sampler decides which it may answer from the radius the march is
// sweeping: a bound is admissible only where it cannot change the march's accept or stop decisions, which the
// sampler proves against its own band before answering one.
internal interface ISdfMarchSampler {
    bool TrySample(FixedPosition position, FixedQ4816 radius, out FixedQ4816 distance, out int material, out bool exact);
}

// The one stepped sphere-trace march every query verb runs, shared by the exact evaluator and the banded one so the
// two never diverge in their accept and advance rules. Two asymmetric uses of the field value, and they are NOT
// interchangeable:
//   accept  — the RAW clearance (fieldDistance - radius) against HitEpsilon. The field is an OVERestimate of true
//             distance, so a raw clearance inside the epsilon proves the surface is too.
//   advance — the SCALED lower bound (fieldDistance * stepScale), floored at one fixed-point tick, minus the
//             radius. Scaling the clearance instead ((f - r) * s) is anti-conservative for r > 0: it shrinks the
//             radius by s as well, leaving f/L - r/L, which exceeds the true safe advance f/L - r whenever L > 1.
//             A radius cast whose scaled bound cannot clear its radius resolves conservatively instead of
//             advancing through an unproven gap; see the tick floor at the advance for why a point cast cannot
//             reach that branch.
// Exact and bound samples spend separate budgets: an exact sample counts against exactBudget, the budget the exact
// evaluator's own march has, so a march that never leaves the band answers bit-identically to that evaluator; a bound
// sample counts against boundBudget, which the caller sizes from the smallest advance a bound can produce.
internal static class SdfFieldMarch {
    // The march accept threshold: a sample within this of the surface counts as a hit rather than one more step.
    // Matches the scale of SdfFieldEvaluator.GradientEpsilon (both are "close enough" tolerances against the same
    // fixed-point field) — tighten per-consumer by wrapping a provider, not by editing the shared constant.
    internal static readonly FixedQ4816 HitEpsilon = FixedQ4816.FromDouble(value: 0.001);
    // The skin distance LineOfSight shrinks its probe by, so a target sitting exactly on a surface (the common "is
    // there a clear line to that wall" query) never reads as self-obstructing.
    internal static readonly FixedQ4816 LineOfSightSkin = FixedQ4816.FromDouble(value: 0.05);

    // Fills the non-convergence hit: the last point the march reached, carrying WorldQueryConfidence.Bounded because
    // the answer is a conservative stand-in for a surface never proven, not a measured one.
    internal static MarchOutcome Exhaust(FixedPosition position, FixedQ4816 traveled, int material, out RayHit hit) {
        hit = new RayHit(
            Confidence: WorldQueryConfidence.Bounded,
            Distance: traveled,
            Material: material,
            Normal: FixedVector3.Zero,
            Point: position
        );

        return MarchOutcome.Exhausted;
    }
    // Returns THREE outcomes, not a Boolean, because the third is not a miss: see MarchOutcome. On Exhausted the hit is
    // filled at the last marched point, so a caller that treats non-convergence as an obstruction has the position and
    // travel it needs without re-marching.
    internal static MarchOutcome Run<TSampler>(ref TSampler sampler, FixedPosition origin, FixedVector3 direction, FixedQ4816 maxDistance, FixedQ4816 radius, FixedQ4816 stepScale, int exactBudget, int boundBudget, bool hasShape, out RayHit hit)
        where TSampler : struct, ISdfMarchSampler {
        hit = default;

        var unit = direction.Normalize();

        if (
            (unit == FixedVector3.Zero) ||
            (maxDistance <= FixedQ4816.Zero)
        ) {
            return MarchOutcome.Miss;
        }

        var position = origin;
        var traveled = FixedQ4816.Zero;
        var lastMaterial = 0;
        var exactSamples = 0;
        var boundSamples = 0;

        while (true) {
            if (!sampler.TrySample(
                distance: out var fieldDistance,
                exact: out var exact,
                material: out var material,
                position: position,
                radius: radius
            )) {
                // A shape-free program genuinely has nothing on the ray; anything else is a point the program's frame
                // cannot express, which proves neither hit nor miss.
                return (hasShape
                    ? Exhaust(
                    hit: out hit,
                    material: lastMaterial,
                    position: position,
                    traveled: traveled
                )
                    : MarchOutcome.Miss
                );
            }

            if (exact
                ? (exactSamples >= exactBudget)
                : (boundSamples >= boundBudget)
            ) {
                return Exhaust(
                    hit: out hit,
                    material: lastMaterial,
                    position: position,
                    traveled: traveled
                );
            }

            if (exact) {
                exactSamples++;
            } else {
                boundSamples++;
            }

            lastMaterial = material;

            var clearance = (fieldDistance - radius);

            if (clearance <= HitEpsilon) {
                // Normal is deliberately NOT computed here — see RayHit.Normal's remarks. Call TryFieldGradient at
                // hit.Point if a future consumer needs it.
                hit = new RayHit(
                    Confidence: WorldQueryConfidence.Exact,
                    Distance: traveled,
                    Material: material,
                    Normal: FixedVector3.Zero,
                    Point: position
                );

                return MarchOutcome.Hit;
            }

            if (traveled >= maxDistance) {
                return MarchOutcome.Miss;
            }

            // The tick floor sits on the field, before the radius comes off, and it is what makes this stop condition
            // subordinate to the accept arm for a point cast. The two are otherwise in different units — accept tests
            // the raw field against HitEpsilon (raw 66), this tests the scaled field against zero — and they cross at
            // stepScale 978/65536 (~0.0149): below it a descent stops one raw tick short of a surface the accept arm's
            // own premise places within HitEpsilon + 1 tick, and TryGroundHeight folds that to "no ground" over every
            // column of such a program. Floored, a point advance is at least one tick at every representable step
            // scale, so the only non-accepting end to a point march is a sample budget.
            // Overstep bound: the floor bites only where the proof-backed advance is already under one tick, and one
            // tick (2^-16 world units) is both the smallest step the format expresses and 1/66 of the band the accept
            // arm already calls contact, so a point march passes the true surface by less than one tick and the next
            // iteration accepts on the negative field inside. Geometry thinner than one tick is under the format.
            // Radius casts are bit-identical: for radius >= one tick, max(floor(f*s), tick) - radius <= 0 exactly when
            // floor(f*s) - radius <= 0, so a sweep still stops before advancing into the contact envelope.
            var safeAdvance = (FixedQ4816.Max(
                x: ScaleDistanceDown(
                    distance: fieldDistance,
                    scale: stepScale
                ),
                y: FixedQ4816.Epsilon
            ) - radius);

            if (safeAdvance <= FixedQ4816.Zero) {
                return Exhaust(
                    hit: out hit,
                    material: material,
                    position: position,
                    traveled: traveled
                );
            }

            var step = FixedQ4816.Min(
                x: safeAdvance,
                y: (maxDistance - traveled)
            );

            traveled += step;
            position += (unit * step);
        }
    }
    // Both operands are non-negative on every call. FixedQ4816 multiplication rounds to nearest, which can round a
    // Lipschitz lower bound UP by half a tick and thereby authorize an unproved advance. This directed product floors
    // the widened raw value so the fixed result remains a lower bound. Scale is in [0,1], so the narrowed quotient
    // cannot overflow long.
    internal static FixedQ4816 ScaleDistanceDown(FixedQ4816 distance, FixedQ4816 scale) =>
        FixedQ4816.FromRawBits(value: ((long)((((Int128)distance.Value) * scale.Value) >> FixedQ4816.FractionBitCount)));
}
