using Puck.Physics.Tests.Fixtures;
using Puck.Physics.Tests.TwoBody;
using Puck.Maths;

namespace Puck.Physics.Tests;

/// <summary>
/// The two-body generalization must be a strict superset of today's single-body solver, not a parallel
/// implementation that happens to agree on paper: a two-body contact where one side is static must reproduce
/// <see cref="FixedRigidSolver"/>'s own single-body trajectory bit for bit, for the same geometry driven by the
/// same options.
/// </summary>
public sealed class TwoBodyStaticDegenerationLawTests {
    private const int FloorSourceId = 1;

    [Fact]
    public void TwoBodyStaticSideReproducesTheSingleBodyStepBitForBit() {
        var options = new FixedRigidSolverOptions { RateHz = 60, SubstepCount = 4, };
        var radius = FixedQ4816.FromDouble(value: 0.5d);
        var startHeight = 1.5d;

        // Single-body path: the production solver, its own manifold slot table, and a candidate re-derived from an
        // absolute height the harness itself owns — mirroring the split every SpikeWorld fixture already keeps
        // (solver acquires no absolute position; the fixture applies the committed displacement).
        var singleBody = SpikeBodies.Sphere(
            radius: radius,
            density: FixedQ4816.FromInteger(value: 20L),
            scales: options.Scales
        );
        var singleSolver = new FixedRigidSolver(options: options);
        var singleSlots = new FixedManifoldSlotTable();
        var singleHeight = startHeight;

        // Two-body path: BodyA is a static "ground" (InverseMassRaw zero — never moves, never rotates), BodyB is the
        // same sphere. The normal points from A toward B (ground toward sphere), matching the single-body model's
        // outward-normal convention exactly, so the SAME positive impulse sign convention applies to the one moving
        // body on both sides of this comparison.
        var ground = new FixedRigidBody();
        var twoBodySphere = SpikeBodies.Sphere(
            radius: radius,
            density: FixedQ4816.FromInteger(value: 20L),
            scales: options.Scales
        );
        var twoBodyBodies = new[] { ground, twoBodySphere, };
        var twoBodySolver = new TwoBodySolver(options: options);
        var twoBodyHeight = startHeight;
        var normalImpulseRaw = 0L;
        TwoBodyContact contact;

        for (var step = 1; (step <= 300); ++step) {
            var candidates = new List<FixedContactCandidate> {
                new(
                SourceId: FloorSourceId,
                FeatureId: 0,
                Anchor: new(
                    X: FixedQ4816.Zero,
                    Y: -radius,
                    Z: FixedQ4816.Zero
                ),
                Normal: new(
                    X: FixedQ4816.Zero,
                    Y: FixedQ4816.One,
                    Z: FixedQ4816.Zero
                ),
                Separation: FixedQ4816.FromDouble(value: (singleHeight - ((double)radius)))
            ),
            };

            singleSolver.Step(
                body: singleBody,
                candidates: candidates,
                slots: singleSlots,
                step: step
            );
            singleHeight += ((double)singleBody.DeltaPosition.Y);

            // TwoBodyContact is a fixed-topology rig (its BaseSeparation is derived once, at construction, from
            // RestSeparation): it does not re-derive separation from a body's CUMULATIVE displacement across steps,
            // only from the displacement accumulated WITHIN the current step. A caller driving it across many steps
            // of real motion must therefore rebuild it each step from the current absolute state and carry the
            // warm-start impulse forward — exactly what a fresh FixedContactCandidate already does for the
            // single-body path. This mirrors that discipline instead of presupposing a FixedRigidWorld-shaped
            // candidate-generation seam that does not exist yet.
            contact = new(
                BodyA: 0,
                BodyB: 1,
                AnchorA: FixedVector3.Zero,
                AnchorB: new(
                    X: FixedQ4816.Zero,
                    Y: -radius,
                    Z: FixedQ4816.Zero
                ),
                Normal: new(
                    X: FixedQ4816.Zero,
                    Y: FixedQ4816.One,
                    Z: FixedQ4816.Zero
                ),
                RestSeparation: FixedQ4816.FromDouble(value: (twoBodyHeight - ((double)radius)))
            ) { NormalImpulseRaw = normalImpulseRaw, };

            var twoBodyContacts = new List<TwoBodyContact> { contact, };

            twoBodySolver.Step(
                bodies: twoBodyBodies,
                contacts: twoBodyContacts,
                step: step
            );
            twoBodyHeight += ((double)twoBodySphere.DeltaPosition.Y);
            normalImpulseRaw = contact.NormalImpulseRaw;

            Assert.Equal(
                expected: singleBody.LinearVelocity,
                actual: twoBodySphere.LinearVelocity
            );
            Assert.Equal(
                expected: singleBody.AngularVelocity,
                actual: twoBodySphere.AngularVelocity
            );
            Assert.Equal(
                expected: singleBody.DeltaPosition,
                actual: twoBodySphere.DeltaPosition
            );
            Assert.Equal(
                expected: singleSlots[0].NormalImpulseRaw,
                actual: normalImpulseRaw
            );
        }

        Assert.Equal(
            expected: 0,
            actual: singleSolver.RefusalCount
        );
        Assert.Equal(
            expected: 0,
            actual: twoBodySolver.RefusalCount
        );
    }
}
