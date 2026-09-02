namespace Puck.Physics.Tests;

using Puck.Maths;

/// <summary>
/// Law coverage for <see cref="FixedTetherConstraint"/>: determinism, the exact slack/taut boundary, momentum
/// preservation at the taut transition, pendulum energy under gravity, the one-way body-anchored drag, and reel-in.
/// </summary>
public sealed class TetherConstraintLawTests {
    private static readonly FixedVector3 Gravity = new(
        X: FixedQ4816.Zero,
        Y: FixedQ4816.FromDouble(value: -9.81d),
        Z: FixedQ4816.Zero
    );
    private static readonly FixedQ4816 Dt = FixedQ4816.FromDouble(value: (1d / 240d));

    // Semi-implicit Euler: velocity absorbs gravity first, then position absorbs the resulting velocity.
    private static void IntegrateFreeStep(ref FixedVector3 position, ref FixedVector3 velocity) {
        velocity += (Gravity * Dt);
        position += (velocity * Dt);
    }
    private static FixedQ4816 TotalEnergy(FixedVector3 position, FixedVector3 velocity) => ((velocity.LengthSquared / FixedQ4816.FromInteger(value: 2L)) - (Gravity.Y * position.Y));

    [Fact]
    public void Determinism_IdenticalRunsProduceBitIdenticalTrajectories() {
        var anchor = FixedVector3.Zero;

        (List<FixedVector3> Positions, List<FixedVector3> Velocities) RunTrajectory() {
            var tether = new FixedTetherConstraint(
                length: FixedQ4816.FromInteger(value: 5L),
                minLength: FixedQ4816.FromInteger(value: 1L)
            );
            var position = new FixedVector3(
                X: FixedQ4816.FromInteger(value: 3L),
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero
            );
            var velocity = new FixedVector3(
                X: FixedQ4816.Zero,
                Y: FixedQ4816.FromInteger(value: 2L),
                Z: FixedQ4816.FromInteger(value: 4L)
            );
            var positions = new List<FixedVector3>();
            var velocities = new List<FixedVector3>();

            for (var tick = 0; (tick < 1000); tick++) {
                IntegrateFreeStep(
                    position: ref position,
                    velocity: ref velocity
                );
                tether.Solve(
                    anchor: in anchor,
                    position: ref position,
                    velocity: ref velocity
                );
                positions.Add(item: position);
                velocities.Add(item: velocity);
            }

            return (positions, velocities);
        }

        var first = RunTrajectory();
        var second = RunTrajectory();

        Assert.Equal(
            actual: second.Positions,
            expected: first.Positions
        );
        Assert.Equal(
            actual: second.Velocities,
            expected: first.Velocities
        );
    }
    [Fact]
    public void Slack_SingleCallLeavesStateUntouched() {
        var anchor = FixedVector3.Zero;
        var tether = new FixedTetherConstraint(
            length: FixedQ4816.FromInteger(value: 10L),
            minLength: FixedQ4816.FromInteger(value: 1L)
        );
        var position = new FixedVector3(
            X: FixedQ4816.FromInteger(value: 6L),
            Y: FixedQ4816.FromInteger(value: 3L),
            Z: FixedQ4816.Zero
        );
        var velocity = new FixedVector3(
            X: FixedQ4816.FromInteger(value: -2L),
            Y: FixedQ4816.FromInteger(value: 5L),
            Z: FixedQ4816.FromInteger(value: 1L)
        );
        var expectedPosition = position;
        var expectedVelocity = velocity;

        var result = tether.Solve(
            anchor: in anchor,
            position: ref position,
            velocity: ref velocity
        );

        Assert.False(condition: result.Taut);
        Assert.Equal(
            actual: position,
            expected: expectedPosition
        );
        Assert.Equal(
            actual: velocity,
            expected: expectedVelocity
        );
    }
    [Fact]
    public void Slack_TrajectoryIsBitIdenticalToTheUnconstrainedRun() {
        var anchor = FixedVector3.Zero;
        // Never reached: the free-fall trajectory below never leaves this radius.
        var length = FixedQ4816.FromInteger(value: 100L);
        var minLength = FixedQ4816.FromInteger(value: 1L);

        (FixedVector3 Position, FixedVector3 Velocity) InitialState() => (
            new FixedVector3(
                X: FixedQ4816.FromInteger(value: 2L),
                Y: FixedQ4816.FromInteger(value: 5L),
                Z: FixedQ4816.Zero
            ),
            new FixedVector3(
                X: FixedQ4816.FromInteger(value: 1L),
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.FromInteger(value: 1L)
            )
        );

        var (withPosition, withVelocity) = InitialState();
        var tether = new FixedTetherConstraint(
            length: length,
            minLength: minLength
        );

        for (var tick = 0; (tick < 500); tick++) {
            IntegrateFreeStep(
                position: ref withPosition,
                velocity: ref withVelocity
            );
            tether.Solve(
                anchor: in anchor,
                position: ref withPosition,
                velocity: ref withVelocity
            );
        }

        var (withoutPosition, withoutVelocity) = InitialState();

        for (var tick = 0; (tick < 500); tick++) {
            IntegrateFreeStep(
                position: ref withoutPosition,
                velocity: ref withoutVelocity
            );
        }

        Assert.Equal(
            actual: withPosition,
            expected: withoutPosition
        );
        Assert.Equal(
            actual: withVelocity,
            expected: withoutVelocity
        );
    }
    [Fact]
    public void Taut_NeverExceedsLengthOverALongSwing() {
        var anchor = FixedVector3.Zero;
        var length = FixedQ4816.FromInteger(value: 5L);
        var tether = new FixedTetherConstraint(
            length: length,
            minLength: FixedQ4816.FromInteger(value: 1L)
        );
        // Hanging straight below the anchor with a horizontal kick: gravity is purely radial (outward) at this
        // point, so the rope goes taut on the very first integrated step and stays taut through the whole swing.
        var position = new FixedVector3(
            X: FixedQ4816.Zero,
            Y: -length,
            Z: FixedQ4816.Zero
        );
        var velocity = new FixedVector3(
            X: FixedQ4816.FromInteger(value: 6L),
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.Zero
        );
        // Bounds the compounded rounding of one Sqrt, one divide and one multiply against a small integer length —
        // not a physical slack, a floor on how tightly a single closed-form projection can land on the sphere.
        var maxAllowedDistance = (length + FixedQ4816.FromDouble(value: 0.01d));
        var maxAllowedDistanceSquared = (maxAllowedDistance * maxAllowedDistance);
        var maxObservedDistanceSquared = FixedQ4816.Zero;

        for (var tick = 0; (tick < 5000); tick++) {
            IntegrateFreeStep(
                position: ref position,
                velocity: ref velocity
            );
            tether.Solve(
                anchor: in anchor,
                position: ref position,
                velocity: ref velocity
            );

            var distanceSquared = (position - anchor).LengthSquared;

            if (distanceSquared > maxObservedDistanceSquared) {
                maxObservedDistanceSquared = distanceSquared;
            }
        }

        Assert.True(
            condition: (maxObservedDistanceSquared <= maxAllowedDistanceSquared),
            userMessage: $"max |body-anchor|^2 observed {maxObservedDistanceSquared} exceeds the allowed {maxAllowedDistanceSquared} (length {length})."
        );
    }
    [Fact]
    public void Taut_PreservesTheNonRadialVelocityComponentsExactly() {
        // Axis-aligned so the whole computation — sqrt of a perfect square, division of a value by itself — is
        // exact fixed-point arithmetic with no rounding anywhere, making this an exact (not tolerance-based) check.
        var anchor = FixedVector3.Zero;
        var length = FixedQ4816.FromInteger(value: 5L);
        var tether = new FixedTetherConstraint(
            length: length,
            minLength: FixedQ4816.FromInteger(value: 1L)
        );
        var position = new FixedVector3(
            X: FixedQ4816.FromInteger(value: 6L),
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.Zero
        );
        var velocity = new FixedVector3(
            X: FixedQ4816.FromInteger(value: 10L),
            Y: FixedQ4816.FromInteger(value: -2L),
            Z: FixedQ4816.FromInteger(value: 3L)
        );

        var result = tether.Solve(
            anchor: in anchor,
            position: ref position,
            velocity: ref velocity
        );

        Assert.True(condition: result.Taut);
        Assert.Equal(
            expected: new FixedVector3(
                X: FixedQ4816.FromInteger(value: 5L),
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero
            ),
            actual: position
        );
        // Only the radial (X) component is removed — the entire outward radial speed, exactly, since it was
        // already the only nonzero component along the (1,0,0) radial direction. Y and Z (the tangential plane
        // here) pass through bit for bit.
        Assert.Equal(
            expected: new FixedVector3(
                X: FixedQ4816.Zero,
                Y: FixedQ4816.FromInteger(value: -2L),
                Z: FixedQ4816.FromInteger(value: 3L)
            ),
            actual: velocity
        );
    }
    [Fact]
    public void Taut_NeverRemovesInwardRadialVelocity() {
        // Position already sits beyond the cap (as if displaced there by something other than this constraint's
        // own integration), but velocity already points back toward the anchor. Solve must still pull the
        // position back onto the sphere, but must leave the already-inward velocity untouched.
        var anchor = FixedVector3.Zero;
        var length = FixedQ4816.FromInteger(value: 5L);
        var tether = new FixedTetherConstraint(
            length: length,
            minLength: FixedQ4816.FromInteger(value: 1L)
        );
        var position = new FixedVector3(
            X: FixedQ4816.FromInteger(value: 6L),
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.Zero
        );
        var velocity = new FixedVector3(
            X: FixedQ4816.FromInteger(value: -3L),
            Y: FixedQ4816.FromInteger(value: 1L),
            Z: FixedQ4816.Zero
        );
        var expectedVelocity = velocity;

        var result = tether.Solve(
            anchor: in anchor,
            position: ref position,
            velocity: ref velocity
        );

        Assert.True(condition: result.Taut);
        Assert.Equal(
            expected: new FixedVector3(
                X: FixedQ4816.FromInteger(value: 5L),
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero
            ),
            actual: position
        );
        Assert.Equal(
            actual: velocity,
            expected: expectedVelocity
        );
    }
    [Fact]
    public void Pendulum_TheConstraintNeverInjectsEnergyAtACorrection() {
        var anchor = FixedVector3.Zero;
        var length = FixedQ4816.FromInteger(value: 5L);
        var tether = new FixedTetherConstraint(
            length: length,
            minLength: FixedQ4816.FromInteger(value: 1L)
        );
        var position = new FixedVector3(
            X: FixedQ4816.Zero,
            Y: -length,
            Z: FixedQ4816.Zero
        );
        var velocity = new FixedVector3(
            X: FixedQ4816.FromInteger(value: 6L),
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.Zero
        );
        var initialEnergy = TotalEnergy(
            position: position,
            velocity: velocity
        );
        var maxObservedEnergy = initialEnergy;

        for (var tick = 0; (tick < 5000); tick++) {
            IntegrateFreeStep(
                position: ref position,
                velocity: ref velocity
            );
            tether.Solve(
                anchor: in anchor,
                position: ref position,
                velocity: ref velocity
            );

            var energy = TotalEnergy(
                position: position,
                velocity: velocity
            );

            if (energy > maxObservedEnergy) {
                maxObservedEnergy = energy;
            }
        }

        // A tolerance covers the free-fall integrator's own bounded (non-monotonic) discretization jitter — the
        // property this measures is that the CONSTRAINT is not a net energy source over the whole swing, not that
        // semi-implicit Euler alone conserves energy tick to tick.
        var tolerance = FixedQ4816.FromDouble(value: 0.5d);

        Assert.True(
            condition: (maxObservedEnergy <= (initialEnergy + tolerance)),
            userMessage: $"max total energy {maxObservedEnergy} exceeds the initial {initialEnergy} by more than {tolerance} over the swing."
        );
    }
    [Fact]
    public void BodyAnchored_DragsTheTetheredBodyAndLeavesTheAnchorUntouched() {
        var length = FixedQ4816.FromInteger(value: 4L);
        var epsilon = FixedQ4816.FromDouble(value: 0.01d);
        var maxAllowedDistanceSquared = ((length + epsilon) * (length + epsilon));
        var anchorVelocity = new FixedVector3(
            X: FixedQ4816.FromInteger(value: 3L),
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.Zero
        );

        // The anchor's own trajectory, computed with NO reference to the tether or the tethered body at all — the
        // independent oracle "moving the anchor body" reduces to.
        FixedVector3 AnchorPositionAtTick(int tick) => (anchorVelocity * (Dt * FixedQ4816.FromInteger(value: tick)));

        var tether = new FixedTetherConstraint(
            length: length,
            minLength: FixedQ4816.FromInteger(value: 1L)
        );
        // Starts at rest, directly "below" the anchor's start in the tether's own local sense (using X as the drag
        // axis here since the anchor travels along +X).
        var bodyPosition = new FixedVector3(
            X: -length,
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.Zero
        );
        var bodyVelocity = FixedVector3.Zero;

        for (var tick = 1; (tick <= 400); tick++) {
            var anchorPosition = AnchorPositionAtTick(tick: tick);

            // No gravity here: isolating the drag itself, not folding in the earlier pendulum-energy scenario.
            bodyPosition += (bodyVelocity * Dt);

            var result = tether.Solve(
                anchor: in anchorPosition,
                position: ref bodyPosition,
                velocity: ref bodyVelocity
            );
            var distanceSquared = (bodyPosition - anchorPosition).LengthSquared;

            Assert.True(
                condition: (distanceSquared <= maxAllowedDistanceSquared),
                userMessage: $"tick {tick}: |body-anchor|^2 {distanceSquared} exceeds the allowed {maxAllowedDistanceSquared}."
            );

            // Solve took the anchor by `in`: re-reading it against the independent oracle proves nothing inside
            // Solve could have written it, not merely that the signature forbids it.
            Assert.Equal(
                expected: AnchorPositionAtTick(tick: tick),
                actual: anchorPosition
            );
            _ = result;
        }

        // The body was dragged along: after 400 ticks of a +X-moving anchor it is far from its -length start.
        Assert.True(
            condition: (bodyPosition.X > FixedQ4816.Zero),
            userMessage: $"expected the tethered body to have been dragged past the origin; ended at {bodyPosition}."
        );
    }
    [Fact]
    public void Reel_InShortensDistanceMonotonicallyAndStopsAtTheFloor() {
        var anchor = FixedVector3.Zero;
        var length = FixedQ4816.FromInteger(value: 10L);
        var minLength = FixedQ4816.FromInteger(value: 2L);
        var tether = new FixedTetherConstraint(
            length: length,
            minLength: minLength
        );
        // Sits exactly at the rope's end; with no other velocity, reeling alone is what drives it inward.
        var position = new FixedVector3(
            X: length,
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.Zero
        );
        var velocity = FixedVector3.Zero;
        var reelRate = -FixedQ4816.FromInteger(value: 3L); // 3 units/second, reeling in
        var previousDistanceSquared = (position - anchor).LengthSquared;
        var reachedFloor = false;
        // Reeling from 10 down to the 2-unit floor at 3 units/second takes 8/3 s; one fixed simulation step at
        // Puck.World's 240 Hz default is 210 of FixedTickConversion's 50400/s engine ticks (matching how a real
        // per-tick caller would drive this), so 700 steps (2.917 s) comfortably clears it.
        const ulong stepEngineTicks = 210UL;

        for (var tick = 0; (tick < 700); tick++) {
            tether.Reel(
                elapsedTicks: stepEngineTicks,
                ratePerSecond: reelRate
            );
            tether.Solve(
                anchor: in anchor,
                position: ref position,
                velocity: ref velocity
            );

            var distanceSquared = (position - anchor).LengthSquared;

            Assert.True(
                condition: (distanceSquared <= previousDistanceSquared),
                userMessage: $"tick {tick}: |body-anchor|^2 grew from {previousDistanceSquared} to {distanceSquared} while reeling in."
            );

            previousDistanceSquared = distanceSquared;

            if (tether.Length == minLength) {
                reachedFloor = true;
            }
        }

        Assert.True(condition: reachedFloor);
        Assert.Equal(
            expected: minLength,
            actual: tether.Length
        );
        Assert.Equal(
            actual: previousDistanceSquared,
            expected: (minLength * minLength)
        );
    }
    [Fact]
    public void Reel_OneCallOverManyTicksMatchesManyOneTickCalls() {
        var length = FixedQ4816.FromInteger(value: 20L);
        var minLength = FixedQ4816.Zero;
        var rate = FixedQ4816.FromDouble(value: 0.37d); // deliberately not an exact multiple of one raw unit/tick
        var incremental = new FixedTetherConstraint(
            length: length,
            minLength: minLength
        );

        for (var tick = 0; (tick < 2100); tick++) {
            incremental.Reel(
                elapsedTicks: 1UL,
                ratePerSecond: rate
            );
        }

        var batched = new FixedTetherConstraint(
            length: length,
            minLength: minLength
        );

        batched.Reel(
            elapsedTicks: 2100UL,
            ratePerSecond: rate
        );

        Assert.Equal(
            expected: batched.Length,
            actual: incremental.Length
        );
    }
    [Fact]
    public void CaptureState_RestoresTheExactNextReelFraction() {
        var rate = FixedQ4816.FromDouble(value: 0.37d);
        var uninterrupted = new FixedTetherConstraint(
            length: FixedQ4816.FromInteger(value: 20L),
            minLength: FixedQ4816.Zero
        );

        for (var tick = 0; (tick < 7); tick++) {
            uninterrupted.Reel(
                elapsedTicks: 210UL,
                ratePerSecond: rate
            );
        }

        var restored = FixedTetherConstraint.FromState(state: uninterrupted.CaptureState());

        Assert.Equal(expected: uninterrupted.CaptureState(), actual: restored.CaptureState());

        for (var tick = 0; (tick < 240); tick++) {
            uninterrupted.Reel(
                elapsedTicks: 210UL,
                ratePerSecond: rate
            );
            restored.Reel(
                elapsedTicks: 210UL,
                ratePerSecond: rate
            );

            Assert.Equal(expected: uninterrupted.CaptureState(), actual: restored.CaptureState());
        }
    }
    [Fact]
    public void ResolveAnchor_RotatesTheLocalOffsetByTheAnchorBodysOrientation() {
        var anchorPosition = new FixedVector3(
            X: FixedQ4816.FromInteger(value: 1L),
            Y: FixedQ4816.FromInteger(value: 2L),
            Z: FixedQ4816.FromInteger(value: 3L)
        );
        var quarterTurnAboutY = FixedQuaternion.FromAxisAngle(
            angle: FixedQ4816.FromDouble(value: (Math.PI / 2d)),
            axis: new FixedVector3(
                X: FixedQ4816.Zero,
                Y: FixedQ4816.One,
                Z: FixedQ4816.Zero
            )
        );
        var localOffset = new FixedVector3(
            X: FixedQ4816.FromInteger(value: 1L),
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.Zero
        );

        var resolved = FixedTetherConstraint.ResolveAnchor(
            anchorOrientation: in quarterTurnAboutY,
            anchorPosition: in anchorPosition,
            localOffset: in localOffset
        );
        var expected = (anchorPosition + quarterTurnAboutY.Rotate(vector: localOffset));

        Assert.Equal(
            actual: resolved,
            expected: expected
        );
        // The offset actually moved relative to a bare translation — proves the orientation was applied, not
        // silently ignored.
        Assert.NotEqual(
            actual: resolved,
            expected: (anchorPosition + localOffset)
        );
    }
}
